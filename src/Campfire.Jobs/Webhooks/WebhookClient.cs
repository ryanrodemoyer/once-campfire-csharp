using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Campfire.Jobs.RestrictedHttp;
using Campfire.RichText.Sanitize;

namespace Campfire.Jobs.Webhooks;

/// <summary>What a bot's webhook answered: its status (null when it timed out) and the reply it makes.</summary>
public sealed record WebhookDelivery(int? Status, WebhookReply? Reply);

/// <summary>The message a bot posts in reply, if any.</summary>
public abstract record WebhookReply;

/// <summary>
/// <c>receive_text_reply_to(room, text:)</c>: the response body, as is, read as UTF-8
/// (<c>force_encoding</c>, so <see cref="Bytes"/> may not be valid UTF-8).
/// </summary>
public sealed record WebhookTextReply(byte[] Bytes) : WebhookReply
{
    public WebhookTextReply(string text) : this(Encoding.UTF8.GetBytes(text))
    {
    }

    /// <summary>The text, with any invalid UTF-8 replaced.</summary>
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// <c>receive_attachment_reply_to(room, attachment:)</c> with the blob
/// <c>ActiveStorage::Blob.create_and_upload!(io:, filename:, content_type:)</c> makes of the body:
/// the filename is <c>"attachment.#{mime_type.symbol}"</c> ("attachment." for an unregistered
/// type) and the content type <c>mime_type.to_s</c>.
/// </summary>
public sealed record WebhookAttachmentReply(byte[] Data, string Filename, string ContentType) : WebhookReply;

/// <summary>
/// <c>Webhook#deliver</c>'s HTTP half (reference/app/models/webhook.rb): POSTs the payload as JSON
/// with <c>Net::HTTP</c>'s headers, then reads the answer: a 200 <c>text/html</c> or
/// <c>text/plain</c> is a text reply, any other content type an attachment, and no content type
/// no reply. Connecting and each read time out after <see cref="EndpointTimeout"/>, which is
/// itself answered with a text reply.
/// <para>
/// Unrestricted by design (<see cref="RestrictedHttpHandlers.Webhook"/>): only an administrator
/// sets the URL. Errors Rails doesn't rescue (a bad URL, a refused connection, a content type that
/// isn't a MIME type) propagate, failing the job.
/// </para>
/// </summary>
public sealed class WebhookClient : IDisposable
{
    /// <summary><c>Webhook::ENDPOINT_TIMEOUT</c></summary>
    public static readonly TimeSpan EndpointTimeout = RestrictedHttpHandlers.WebhookTimeout;

    readonly HttpMessageInvoker http;
    readonly TimeSpan timeout;

    /// <summary>
    /// Bodies are inflated here rather than by the handler, which would rewrite Net::HTTP's
    /// <c>Accept-Encoding</c> in its own format. <paramref name="timeout"/> is for tests.
    /// </summary>
    public WebhookClient(TimeSpan? timeout = null)
    {
        var handler = RestrictedHttpHandlers.Webhook();
        handler.AutomaticDecompression = DecompressionMethods.None;
        this.timeout = timeout ?? EndpointTimeout;
        handler.ConnectTimeout = this.timeout;
        http = new HttpMessageInvoker(handler);
    }

    public void Dispose() => http.Dispose();

    /// <summary><c>deliver</c>: posts <paramref name="payload"/> to <paramref name="url"/> and reads the reply.</summary>
    public async Task<WebhookDelivery> DeliverAsync(string? url, string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = Post(url, payload);
        try
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var contentType = ContentType(response);
            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            return new WebhookDelivery(status, Reply(status, contentType, body));
        }
        catch (Exception error) when (IsTimeout(error, cancellationToken))
        {
            // `rescue Net::OpenTimeout, Net::ReadTimeout`
            return new WebhookDelivery(null, new WebhookTextReply($"Failed to respond within {(int)timeout.TotalSeconds} seconds"));
        }
    }

    /// <summary><c>extract_text_from(response)</c>, else <c>extract_attachment_from(response)</c>.</summary>
    public static WebhookReply? Reply(int status, string? contentType, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (contentType is null)
        {
            return null;
        }
        if (status == 200 && contentType is "text/html" or "text/plain")
        {
            return new WebhookTextReply(body);
        }
        var mimeType = WebhookMimeTypes.Find(contentType);
        return new WebhookAttachmentReply(body, $"attachment.{mimeType.Symbol}", mimeType.MediaType);
    }

    /// <summary>
    /// <c>Net::HTTP::Post.new(uri, "Content-Type" =&gt; "application/json")</c> with the payload, sent
    /// over <c>Net::HTTP.new(uri.host, uri.port)</c>: the default <c>Accept-Encoding</c>,
    /// <c>Accept</c>, <c>User-Agent</c> and <c>Host</c>, and <c>Connection: close</c>, as a
    /// connection that wasn't started sends.
    /// </summary>
    static HttpRequestMessage Post(string? url, string payload)
    {
        // `URI(url)`: nil isn't a URI.
        var uri = RubyUri.Parse(url ?? throw new ArgumentException("bad argument (expected URI object or URI string)"));
        if (!uri.IsHttp)
        {
            throw new ArgumentException("not an HTTP URI");
        }
        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException("no host component for URI");
        }
        if (uri.Port is not { } port || port > ushort.MaxValue)
        {
            throw new ArgumentException($"invalid port: {uri.Port}");
        }
        var https = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var target = new Uri(
            $"{(https ? "https" : "http")}://{uri.Host}:{port}{RequestUri(uri)}",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        var request = new HttpRequestMessage(HttpMethod.Post, target) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip;q=1.0,deflate;q=0.6,identity;q=0.3");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("User-Agent", "Ruby");
        request.Headers.TryAddWithoutValidation("Host", HostHeader(uri.Host, port, https));
        request.Headers.ConnectionClose = true;
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    // The response head must arrive within the timeout, as Net::HTTP's read_timeout bounds each
    // read of it.
    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        return await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
    }

    /// <summary><c>response.body</c>: every byte, inflated if Net::HTTP asked for compression and got it, each read within the timeout.</summary>
    async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
                body.Write(buffer, 0, read);
            }
        }
    }

    CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    // A cancellation that isn't the caller's is one of ours: the connect or a read timed out.
    static bool IsTimeout(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error switch
        {
            OperationCanceledException or TimeoutException => true,
            HttpRequestException { InnerException: { } inner } => inner is OperationCanceledException or TimeoutException,
            _ => false,
        };

    /// <summary><c>URI::HTTP#request_uri</c>: the path (<c>/</c> when empty) and the query.</summary>
    static string RequestUri(RubyUri uri)
    {
        var path = string.IsNullOrEmpty(uri.Path) ? "/" : uri.Path;
        return uri.Query is null ? path : $"{path}?{uri.Query}";
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

    /// <summary><c>Net::HTTPResponse#inflater</c>: gzip and deflate bodies are inflated, anything else read as it is.</summary>
    static Stream Inflated(HttpResponseMessage response, Stream body) =>
        Header(response, "Content-Encoding")?.ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => new GZipStream(body, CompressionMode.Decompress),
            "deflate" => new ZLibStream(body, CompressionMode.Decompress),
            _ => body,
        };

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

    /// <summary><c>Net::HTTPHeader#[]</c>: every value of the header joined with ", ", as received.</summary>
    static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.NonValidated.TryGetValues(name, out var values) || response.Content.Headers.NonValidated.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }
        return null;
    }

    /// <summary><c>String#strip</c>: ASCII whitespace from both ends, and NULs from the end.</summary>
    static string RubyStrip(string value) => value.TrimStart(" \t\n\v\f\r".ToCharArray()).TrimEnd(" \t\n\v\f\r\0".ToCharArray());
}
