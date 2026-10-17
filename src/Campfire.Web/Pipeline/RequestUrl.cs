using System.Globalization;
using System.Text.RegularExpressions;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Pipeline;

/// <summary>
/// Where a request says it came to: <c>ActionDispatch::Http::URL</c> (<c>protocol</c>, <c>host</c>,
/// <c>port</c>, <c>url</c>) over Rack's <c>scheme</c>, which believes <c>X-Forwarded-Ssl</c>,
/// <c>Forwarded: proto=</c> and <c>X-Forwarded-Proto</c>. <c>config.assume_ssl</c>
/// (<c>ActionDispatch::AssumeSSL</c>) makes every request HTTPS.
/// </summary>
public sealed partial class RequestUrl
{
    static readonly string[] AllowedSchemes = ["https", "http", "wss"];

    readonly HttpRequest request;
    readonly bool assumeSsl;
    string? scheme;
    string? rawHostWithPort;
    string? host;
    int? port;
    string? baseUrl;

    public RequestUrl(HttpRequest request, bool assumeSsl = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.request = request;
        this.assumeSsl = assumeSsl;
    }

    /// <summary>Rack's <c>scheme</c>: <c>https</c>, <c>http</c> or <c>wss</c>.</summary>
    public string Scheme => scheme ??= ResolveScheme();

    /// <summary><c>request.ssl?</c></summary>
    public bool IsSsl => Scheme is "https" or "wss";

    /// <summary><c>request.protocol</c>: <c>https://</c> or <c>http://</c>.</summary>
    public string Protocol => IsSsl ? "https://" : "http://";

    /// <summary>
    /// <c>raw_host_with_port</c>: the last <c>X-Forwarded-Host</c>, else the <c>Host</c> header, else
    /// the server's name and port.
    /// </summary>
    public string RawHostWithPort => rawHostWithPort ??= ResolveRawHostWithPort();

    /// <summary><c>request.host</c>: <see cref="RawHostWithPort"/> without a trailing <c>:port</c>.</summary>
    public string Host => host ??= WithoutTrailingPort(RawHostWithPort);

    /// <summary><c>request.port</c>: the host's port, else the protocol's.</summary>
    public int Port => port ??= ResolvePort();

    public int StandardPort => IsSsl ? 443 : 80;

    /// <summary><c>request.optional_port</c>: the port unless it's the protocol's own.</summary>
    public int? OptionalPort => Port == StandardPort ? null : Port;

    /// <summary><c>request.host_with_port</c></summary>
    public string HostWithPort => OptionalPort is { } port ? $"{Host}:{port.ToString(CultureInfo.InvariantCulture)}" : Host;

    /// <summary><c>request.base_url</c>, for example <c>http://campfire.test</c>.</summary>
    public string BaseUrl => baseUrl ??= Protocol + HostWithPort;

    /// <summary>
    /// Rack's <c>fullpath</c>: the raw path (<c>SCRIPT_NAME</c> + <c>PATH_INFO</c>) and the query
    /// string, if any.
    /// </summary>
    public string FullPath
    {
        get
        {
            var path = request.HttpContext.Features.Get<RailsRequest>()?.OriginalPath ?? (request.PathBase + request.Path).ToUriComponent();
            var query = request.QueryString.HasValue ? request.QueryString.Value![1..] : "";
            return query.Length == 0 ? path : $"{path}?{query}";
        }
    }

    /// <summary><c>request.url</c></summary>
    public string Url => BaseUrl + FullPath;

    /// <summary>
    /// What <c>_url</c> helpers build on: <c>SetCurrentRequest#default_url_options</c>
    /// (<c>request.host</c> and <c>request.protocol</c>) plus ActionController's
    /// <c>request.optional_port</c>.
    /// </summary>
    public UrlBase UrlBase => new(Protocol, Host, OptionalPort);

    string ResolveScheme()
    {
        if (assumeSsl || request.IsHttps)
        {
            return "https";
        }
        if (Header("X-Forwarded-Ssl") == "on")
        {
            return "https";
        }
        return ForwardedScheme() ?? request.Scheme;
    }

    // forwarded_scheme: Forwarded's last proto, then X-Forwarded-Proto and X-Forwarded-Scheme,
    // each read right to left for the first allowed scheme.
    string? ForwardedScheme()
    {
        if (ForwardedHeader.Values(Header("Forwarded"), "proto") is { Count: > 0 } protos)
        {
            if (Allowed(protos[^1]) is { } scheme)
            {
                return scheme;
            }
        }
        foreach (var name in (ReadOnlySpan<string>)["X-Forwarded-Proto", "X-Forwarded-Scheme"])
        {
            var values = ForwardedHeader.SplitHeader(Header(name));
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (Allowed(values[i]) is { } scheme)
                {
                    return scheme;
                }
            }
        }
        return null;
    }

    static string? Allowed(string scheme) => Array.IndexOf(AllowedSchemes, scheme) >= 0 ? scheme : null;

    string ResolveRawHostWithPort()
    {
        var forwarded = Header("X-Forwarded-Host");
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            // forwarded.split(/,\s?/).last
            var parts = ForwardedHostSeparator().Split(forwarded);
            var count = parts.Length;
            while (count > 0 && parts[count - 1].Length == 0)
            {
                count--;
            }
            return count == 0 ? "" : parts[count - 1];
        }
        if (Header("Host") is { } host)
        {
            return host;
        }
        var connection = request.HttpContext.Connection;
        return $"{connection.LocalIpAddress}:{connection.LocalPort.ToString(CultureInfo.InvariantCulture)}";
    }

    string? Header(string name) => request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    int ResolvePort() =>
        TrailingPort(RawHostWithPort, out _, out var digits) && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : StandardPort;

    static string WithoutTrailingPort(string rawHostWithPort) =>
        TrailingPort(rawHostWithPort, out var colon, out var digits)
            ? string.Concat(rawHostWithPort.AsSpan(0, colon), rawHostWithPort.AsSpan(colon + 1 + digits.Length))
            : rawHostWithPort;

    /// <summary>
    /// What <c>/:(\d+)$/</c> finds in a host: a colon and the digits, any Unicode digit as <c>\d</c> is, that run to the end
    /// of the string or to a final newline, where <c>$</c> also matches. The first such colon is the last one, so the
    /// host is read from its end.
    /// </summary>
    static bool TrailingPort(string host, out int colon, out ReadOnlySpan<char> digits)
    {
        var end = host.Length > 0 && host[^1] == '\n' ? host.Length - 1 : host.Length;
        var start = end;
        while (start > 0 && char.IsDigit(host[start - 1]))
        {
            start--;
        }
        if (start < end && start > 0 && host[start - 1] == ':')
        {
            colon = start - 1;
            digits = host.AsSpan(start, end - start);
            return true;
        }
        colon = -1;
        digits = default;
        return false;
    }

    [GeneratedRegex(@",\s?")]
    private static partial Regex ForwardedHostSeparator();
}
