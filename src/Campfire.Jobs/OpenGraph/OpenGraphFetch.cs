using System.IO.Compression;
using System.Net;
using Campfire.Jobs.RestrictedHttp;
using Campfire.RichText.Sanitize;

namespace Campfire.Jobs.OpenGraph;

/// <summary><c>Opengraph::Fetch::TooManyRedirectsError</c></summary>
public sealed class TooManyRedirectsException() : Exception("Opengraph::Fetch::TooManyRedirectsError");

/// <summary><c>Opengraph::Fetch::RedirectDeniedError</c></summary>
public sealed class RedirectDeniedException() : Exception("Opengraph::Fetch::RedirectDeniedError");

/// <summary><c>Net::HTTPHeaderSyntaxError</c> and a <c>Location</c> that <c>URI.parse</c> rejects.</summary>
public sealed class OpenGraphHttpException(string message) : Exception(message);

/// <summary>
/// <c>Opengraph::Fetch</c> (reference/app/models/opengraph/fetch.rb): GET or HEAD against a pinned
/// address, following up to 10 responses (any 3xx is a redirect), each redirect target parsed,
/// required to be http(s), and resolved through the private network guard again. A document must
/// be a 200 <c>text/html</c> of at most 5MB, by <c>Content-Length</c> and by what's actually read.
/// <para>
/// Requests look like <c>Net::HTTP</c>'s: the same <c>Accept</c>, <c>Accept-Encoding</c>,
/// <c>User-Agent</c> and <c>Host</c>, gzip and deflate inflated, and Net::HTTP's 60 second
/// timeouts for connecting, for the response head and for each read of the body.
/// </para>
/// </summary>
public sealed class OpenGraphFetch : IDisposable
{
    public const string AllowedDocumentContentType = "text/html";
    public const int MaxBodySize = 5 * 1024 * 1024;
    public const int MaxRedirects = 10;

    /// <summary><c>Net::HTTP#read_timeout</c>'s default.</summary>
    public static readonly TimeSpan ReadTimeout = RestrictedHttpHandlers.NetHttpDefaultTimeout;

    readonly HttpMessageInvoker http;

    /// <summary>
    /// Through <see cref="RestrictedHttpHandlers.OpenGraph"/>, so only admitted addresses are
    /// dialled. Bodies are inflated here rather than by the handler, which would rewrite
    /// Net::HTTP's <c>Accept-Encoding</c> in its own format.
    /// </summary>
    public OpenGraphFetch(PrivateNetworkGuard guard, Dialer? dial = null)
    {
        var handler = RestrictedHttpHandlers.OpenGraph(guard, dial);
        handler.AutomaticDecompression = DecompressionMethods.None;
        http = new HttpMessageInvoker(handler);
        Guard = guard;
    }

    public PrivateNetworkGuard Guard { get; }

    public void Dispose() => http.Dispose();

    /// <summary>Where <c>Rails.logger.warn</c> goes: the failures the models rescue and log.</summary>
    public Action<string>? Warn { get; init; }

    /// <summary><c>fetch_document(url, ip:)</c>: the body, or null when the response isn't acceptable.</summary>
    public async Task<byte[]?> FetchDocumentAsync(RubyUri url, IPAddress ip, CancellationToken cancellationToken = default)
    {
        using var response = await RequestAsync(url, ip, HttpMethod.Get, cancellationToken).ConfigureAwait(false);
        return IsValid(response) ? await SizeRestrictedBodyAsync(response, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary><c>fetch_content_type(url, ip:)</c>: the final response's <c>Content-Type</c>, whatever its status.</summary>
    public async Task<string?> FetchContentTypeAsync(RubyUri url, IPAddress ip, CancellationToken cancellationToken = default)
    {
        using var response = await RequestAsync(url, ip, HttpMethod.Head, cancellationToken).ConfigureAwait(false);
        return Header(response, "Content-Type");
    }

    async Task<HttpResponseMessage> RequestAsync(RubyUri url, IPAddress ip, HttpMethod method, CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxRedirects; i++)
        {
            var response = await SendAsync(url, ip, method, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is < 300 or > 399)
            {
                return response;
            }
            using (response)
            {
                (url, ip) = await ResolveRedirectAsync(Header(response, "Location"), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TooManyRedirectsException();
    }

    async Task<(RubyUri, IPAddress)> ResolveRedirectAsync(string? location, CancellationToken cancellationToken)
    {
        // URI.parse(nil) raises too.
        var url = RubyUri.Parse(location ?? throw new OpenGraphHttpException("bad URI(is not URI?): nil"));
        if (!url.IsHttp)
        {
            throw new RedirectDeniedException();
        }
        return (url, await Guard.ResolveAsync(url.Host ?? "", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <c>Net::HTTP.start(url.host, url.port, ipaddr: ip, use_ssl: url.scheme == "https")</c> and
    /// <c>http.request(request_class.new(url))</c>.
    /// </summary>
    async Task<HttpResponseMessage> SendAsync(RubyUri url, IPAddress ip, HttpMethod method, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url.Host) || url.Port is not { } port || port > ushort.MaxValue)
        {
            throw new OpenGraphHttpException($"bad URI: {url.ToUriString()}");
        }
        var https = string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var target = new Uri(
            $"{(https ? "https" : "http")}://{url.Host}:{port}{RequestUri(url)}",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        using var request = new HttpRequestMessage(method, target);
        request.Options.Set(RestrictedHttpHandlers.PinnedAddress, ip);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip;q=1.0,deflate;q=0.6,identity;q=0.3");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("User-Agent", "Ruby");
        request.Headers.TryAddWithoutValidation("Host", HostHeader(url.Host, port, https));

        using var timeout = Timeout(cancellationToken);
        return await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
    }

    /// <summary><c>URI::HTTP#request_uri</c>: the path (<c>/</c> when empty) and the query.</summary>
    static string RequestUri(RubyUri url)
    {
        var path = string.IsNullOrEmpty(url.Path) ? "/" : url.Path;
        return url.Query is null ? path : $"{path}?{url.Query}";
    }

    /// <summary>
    /// <c>Net::HTTPGenericRequest#initialize</c>: <c>uri.hostname</c> (an IPv6 literal without its
    /// brackets), plus the port unless it's the scheme's default.
    /// </summary>
    static string HostHeader(string host, ulong port, bool https)
    {
        var hostname = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return port == (https ? 443ul : 80ul) ? hostname : $"{hostname}:{port}";
    }

    static bool IsValid(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK && ContentType(response) == AllowedDocumentContentType && ContentLength(response) <= MaxBodySize;

    /// <summary>
    /// Reads the body in chunks, giving up once it runs over the limit: the
    /// <c>Content-Length</c> header can be wrong or missing.
    /// </summary>
    static async Task<byte[]?> SizeRestrictedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = Inflated(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        await using (stream.ConfigureAwait(false))
        {
            using var body = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int read;
                using (var timeout = Timeout(cancellationToken))
                {
                    read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                }
                if (read == 0)
                {
                    return body.ToArray();
                }
                if (body.Length + read > MaxBodySize)
                {
                    return null;
                }
                body.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>
    /// <c>Net::HTTPResponse#inflater</c>: gzip and deflate bodies are inflated (Net::HTTP asked
    /// for them), anything else is read as it is.
    /// </summary>
    static Stream Inflated(HttpResponseMessage response, Stream body) =>
        Header(response, "Content-Encoding")?.ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => new GZipStream(body, CompressionMode.Decompress),
            "deflate" => new ZLibStream(body, CompressionMode.Decompress),
            _ => body,
        };

    static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        return timeout;
    }

    /// <summary><c>Net::HTTPHeader#[]</c>: every value of the header joined with ", ", as received.</summary>
    static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.NonValidated.TryGetValues(name, out var values) || response.Content.Headers.NonValidated.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }
        return null;
    }

    /// <summary>
    /// <c>Net::HTTPHeader#content_type</c>: the media type before any <c>;</c>, main and sub type
    /// each stripped (not downcased), or just the main type when there's no <c>/</c>.
    /// </summary>
    static string? ContentType(HttpResponseMessage response)
    {
        if (Header(response, "Content-Type") is not { } header)
        {
            return null;
        }
        var parts = header.Split(';')[0].Split('/');
        var main = RubyStrip(parts[0]);
        return parts.Length > 1 ? $"{main}/{RubyStrip(parts[1])}" : main;
    }

    /// <summary>
    /// <c>content_length.to_i</c>: the first run of digits, 0 without the header, or
    /// <c>HTTPHeaderSyntaxError</c>. Read before any inflating, as Net::HTTP does.
    /// </summary>
    static ulong ContentLength(HttpResponseMessage response)
    {
        if (Header(response, "Content-Length") is not { } header)
        {
            return 0;
        }
        var digits = new string([.. header.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit)]);
        if (digits.Length == 0)
        {
            throw new OpenGraphHttpException("wrong Content-Length format");
        }
        return ulong.TryParse(digits, out var length) ? length : ulong.MaxValue;
    }

    /// <summary><c>String#strip</c>: ASCII whitespace from both ends, and NULs from the end.</summary>
    static string RubyStrip(string value) => value.TrimStart(" \t\n\v\f\r".ToCharArray()).TrimEnd(" \t\n\v\f\r\0".ToCharArray());
}
