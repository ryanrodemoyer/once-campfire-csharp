using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.Web.ResponseCache;
using Campfire.Web.Routing;
using ResponseCacheStore = Campfire.Web.ResponseCache.ResponseCache;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>CachedResponses</c> (reference/app/controllers/concerns/cached_responses.rb). The version is
/// captured before authentication. A page is admitted only when that version is still current
/// and the session did not change while the page was rendered. A hit still ran authentication
/// and room authorization; this only replaces the rendered body.
/// </summary>
public partial class ApplicationController
{
    static readonly string[] CachedHeaderNames = ["Content-Type", "Content-Encoding", "Cache-Control", "ETag", "Last-Modified", "Vary"];

    bool? fragmentSnapshotValid;

    /// <summary>The version captured before authentication (<c>@response_cache_version</c>).</summary>
    public CacheVersion? ResponseCacheVersion { get; private set; }

    /// <summary><c>capture_response_cache_version</c>, the first before callback.</summary>
    public void CaptureResponseCacheVersion() => ResponseCacheVersion = App.Responses.Version();

    /// <summary>
    /// <c>perform_caching</c>: a captured version that still matches, and no open transaction.
    /// The comparison happens once, so a later commit cannot be served from the old epoch.
    /// </summary>
    public bool PerformCaching
    {
        get
        {
            if (ResponseCacheVersion is null || FragmentStore.TransactionOpen)
            {
                return false;
            }
            fragmentSnapshotValid ??= App.Responses.Version() == ResponseCacheVersion;
            return fragmentSnapshotValid.Value;
        }
    }

    /// <summary><c>read_record_cache_version</c>: the captured version on GET and HEAD.</summary>
    internal CacheVersion? RecordCacheVersion() =>
        Request.Method is "GET" or "HEAD" ? ResponseCacheVersion : null;

    /// <summary>The fragment namespace for a request render, or null when <see cref="PerformCaching"/> is false.</summary>
    internal FragmentRendering? FragmentContext()
    {
        if (!PerformCaching || ResponseCacheVersion is not { } version)
        {
            return null;
        }
        var session = Current.Session;
        var digest = session is null ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Token)));
        var context = string.Join('\n', RequestUrl.BaseUrl, "", Request.Format?.Value ?? "", "en", Current.User?.Id.ToString(CultureInfo.InvariantCulture) ?? "", digest);
        return new FragmentRendering
        {
            Store = App.Fragments,
            Version = version.Key,
            Context = context,
            BaseUrl = RequestUrl.BaseUrl,
        };
    }

    /// <summary>
    /// <c>cache_read_response</c>. Called inside the action, after the before callbacks, so a
    /// redirect from authentication or <c>set_room</c> never reaches it.
    /// </summary>
    internal async ValueTask CacheReadResponseAsync(Func<ValueTask> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        if (!CacheableRead() || ResponseCacheVersion is not { } version)
        {
            await render().ConfigureAwait(false);
            return;
        }
        var encoding = RackDeflater.SelectBestEncoding(Header("Accept-Encoding"));
        if (encoding is null)
        {
            await render().ConfigureAwait(false);
            return;
        }
        var key = ResponseCacheKey(encoding);
        if (Encoding.UTF8.GetByteCount(key) > ResponseCacheStore.MaxKeyBytes)
        {
            await render().ConfigureAwait(false);
            return;
        }
        var cache = App.Responses;
        var entry = cache.Read(key, version);
        var rendered = false;
        if (entry is null)
        {
            await cache.SynchronizeRenderAsync(key, version, async () =>
            {
                entry = cache.Read(key, version);
                if (entry is null && cache.Version() == version)
                {
                    var originalSession = Session.CanonicalHash(exceptCsrf: false);
                    await render().ConfigureAwait(false);
                    rendered = true;
                    entry = CompleteCachedResponse(cache, key, version, originalSession, encoding);
                }
            }).ConfigureAwait(false);
        }
        if (entry is not null)
        {
            ApplyCached(entry);
        }
        else if (!rendered)
        {
            // The queued request kept its pre-auth version. It has expired, so render outside
            // the lock instead of holding the next generation.
            await render().ConfigureAwait(false);
        }
    }

    bool CacheableRead() =>
        App.Responses.Budget > 0
        && ResponseCacheVersion is not null
        && Request.Method == "GET"
        && Request.Format == MimeType.Html
        && Current.Session is not null
        && AuthenticatedBy != AuthenticatedBy.BotKey
        && Flash.IsEmpty
        && !Present(Header("If-None-Match"))
        && !Present(Header("If-Modified-Since"))
        && !FragmentStore.TransactionOpen;

    CachedPage? CompleteCachedResponse(ResponseCacheStore cache, string key, CacheVersion version, string originalSession, string encoding)
    {
        if (Status != 200 || MediaTypeOf(ContentType) != "text/html" || Session.CanonicalHash(exceptCsrf: false) != originalSession || Headers["Content-Encoding"] is not null)
        {
            return null;
        }
        var raw = Body.ToArray();
        var stored = encoding == "gzip" ? RackDeflater.GzipBody(raw) : raw;
        if (stored.Length > 0 && Headers["ETag"] is null && Headers["Last-Modified"] is null)
        {
            Headers["ETag"] = $"W/\"{Convert.ToHexStringLower(SHA256.HashData(stored))[..32]}\"";
        }
        if (encoding == "gzip")
        {
            Headers["Content-Encoding"] = "gzip";
        }
        Headers["Vary"] = MergeVary(Headers["Vary"]);
        var page = new CachedPage(stored, SnapshotHeaders());
        cache.Write(key, version, page);
        return page;
    }

    void ApplyCached(CachedPage page)
    {
        foreach (var (name, value) in page.Headers)
        {
            Headers[name] = value;
        }
        Headers["Content-Length"] = null;
        AdoptBody(page.Body);
    }

    List<(string Name, string Value)> SnapshotHeaders()
    {
        var headers = new List<(string, string)>(CachedHeaderNames.Length);
        foreach (var name in CachedHeaderNames)
        {
            if (Headers[name] is { } value)
            {
                headers.Add((name, value));
            }
        }
        return headers;
    }

    // response_cache_key: the JSON array Rails builds. _csrf_token, the session cookie and
    // session_token are left out; the session token itself is a separate element.
    string ResponseCacheKey(string encoding)
    {
        var user = Current.User ?? throw new InvalidOperationException("undefined method 'id' for nil");
        var session = Current.Session ?? throw new InvalidOperationException("undefined method 'token' for nil");
        return string.Concat(
            "[",
            Json(ControllerPath), ",",
            Json(RequestUrl.FullPath), ",",
            Json(RequestUrl.BaseUrl), ",",
            Json(UserAgent), ",",
            Json(encoding), ",",
            Json(Header("Accept")), ",",
            Json(Header("Turbo-Frame")), ",",
            Json("en"), ",",
            user.Id.ToString(CultureInfo.InvariantCulture), ",",
            Json(session.Token), ",",
            Session.CanonicalHash(exceptCsrf: true), ",",
            CookieKey(Cookies),
            "]");
    }

    static string CookieKey(CookieJar cookies)
    {
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var (name, value) in cookies.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (name is "_campfire_session" or "session_token")
            {
                continue;
            }
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            builder.Append(Json(name)).Append(':').Append(Json(value));
        }
        builder.Append('}');
        return builder.ToString();
    }

    static string MergeVary(string? vary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(vary))
        {
            foreach (var part in vary.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!parts.Contains(part, StringComparer.Ordinal))
                {
                    parts.Add(part);
                }
            }
        }
        if (!parts.Contains("Accept-Encoding", StringComparer.Ordinal))
        {
            parts.Add("Accept-Encoding");
        }
        return string.Join(", ", parts);
    }

    static string? MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return media.Length == 0 ? null : media;
    }

    /// <summary>
    /// One string as <c>ActiveSupport::JSON.encode</c> writes it for this app. <c>&lt;</c>,
    /// <c>&gt;</c> and <c>&amp;</c> become <c>\u003c</c>, <c>\u003e</c> and <c>\u0026</c>, so a long
    /// URL or user agent crosses <c>MAX_KEY_BYTES</c> on the same side as Rails. U+2028 and U+2029
    /// stay raw: <c>config.load_defaults 8.2</c> sets <c>escape_js_separators_in_json</c> false
    /// (railties <c>configuration.rb</c>, copied onto <c>ActiveSupport</c> by its railtie).
    /// </summary>
    internal static string Json(string? value) => value is null ? "null" : RailsJson.Encode(JsonValue.Create(value));

    static bool Present(string? value) => !string.IsNullOrEmpty(value);
}
