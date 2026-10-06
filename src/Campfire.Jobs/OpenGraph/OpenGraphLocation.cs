using System.Net;
using System.Text.RegularExpressions;
using Campfire.RichText.Sanitize;

namespace Campfire.Jobs.OpenGraph;

/// <summary>
/// <c>Opengraph::Location</c> (reference/app/models/opengraph/location.rb): a URL that is valid
/// when it parses as http(s) and its host resolves to a public address, which is memoized and
/// pinned for the fetch.
/// </summary>
public sealed partial class OpenGraphLocation
{
    readonly OpenGraphFetch fetch;
    readonly string? url;
    IPAddress? resolvedIp;
    bool resolved;

    /// <summary><c>Location.new(url)</c>: <c>parsed_url</c> is <c>URI.parse(url) rescue nil</c>.</summary>
    public OpenGraphLocation(OpenGraphFetch fetch, string? url)
    {
        this.fetch = fetch;
        this.url = url;
        ParsedUrl = Parse(url);
    }

    public RubyUri? ParsedUrl { get; }

    /// <summary><c>valid?</c>: both validations run, so the host is resolved even for a non-http URL.</summary>
    public async Task<bool> IsValidAsync(CancellationToken cancellationToken = default)
    {
        var http = ParsedUrl?.IsHttp == true;
        var isPublic = await ResolvedIpAsync(cancellationToken).ConfigureAwait(false) is not null;
        return http && isPublic;
    }

    /// <summary><c>resolved_ip</c>: <c>PrivateNetworkGuard.resolve(parsed_url.host) rescue nil</c>, memoized.</summary>
    public async Task<IPAddress?> ResolvedIpAsync(CancellationToken cancellationToken = default)
    {
        if (!resolved)
        {
            resolvedIp = ParsedUrl?.Host is { } host ? await ResolveAsync(host, cancellationToken).ConfigureAwait(false) : null;
            resolved = true;
        }
        return resolvedIp;
    }

    /// <summary><c>read_html</c>: nothing for invalid URLs or ones that look like files and media.</summary>
    public async Task<byte[]?> ReadHtmlAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsValidAsync(cancellationToken).ConfigureAwait(false) || FilesAndMediaUrl().IsMatch(url ?? ""))
        {
            return null;
        }
        var ip = (await ResolvedIpAsync(cancellationToken).ConfigureAwait(false))!;
        try
        {
            return await fetch.FetchDocumentAsync(ParsedUrl!, ip, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFetchFailure(e, cancellationToken))
        {
            fetch.Warn?.Invoke($"Failed to fetch {ParsedUrl!.ToUriString()} at {ip} ({e.Message})");
            return null;
        }
    }

    /// <summary><c>fetch_content_type</c></summary>
    public async Task<string?> FetchContentTypeAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsValidAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var ip = (await ResolvedIpAsync(cancellationToken).ConfigureAwait(false))!;
        try
        {
            return await fetch.FetchContentTypeAsync(ParsedUrl!, ip, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFetchFailure(e, cancellationToken))
        {
            fetch.Warn?.Invoke($"Failed to fetch {ParsedUrl!.ToUriString()} at {ip} ({e.Message})");
            return null;
        }
    }

    async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await fetch.Guard.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFetchFailure(e, cancellationToken))
        {
            return null;
        }
    }

    static RubyUri? Parse(string? url)
    {
        try
        {
            return url is null ? null : RubyUri.Parse(url);
        }
        catch (RubyUriException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>rescue =&gt; e</c>: any failure is a missing page, except the caller giving up, which
    /// is the caller's to handle. A read timing out is a failure like any other.
    /// </summary>
    static bool IsFetchFailure(Exception e, CancellationToken cancellationToken) =>
        !(e is OperationCanceledException && cancellationToken.IsCancellationRequested);

    /// <summary><c>FILES_AND_MEDIA_URL_REGEX</c>, with Ruby's ASCII <c>\b</c> and <c>\S</c>.</summary>
    [GeneratedRegex(@"\bhttps?://\S+\.(?:zip|tar|tar\.gz|tar\.bz2|tar\.xz|gz|bz2|rar|7z|dmg|exe|msi|pkg|deb|iso|jpg|jpeg|png|gif|bmp|mp4|mov|avi|mkv|wmv|flv|heic|heif|mp3|wav|ogg|aac|wma|webm|ogv|mpg|mpeg)\b", RegexOptions.ECMAScript)]
    private static partial Regex FilesAndMediaUrl();
}
