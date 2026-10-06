using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Campfire.Web.Pipeline;

/// <summary>
/// The validators <c>fresh_when</c> and <c>stale?</c> take, each already a cache key
/// (<c>ActiveSupport::Cache.retrieve_cache_key</c>: a record's <c>cache_key_with_version</c>, see
/// <see cref="CacheKeys"/>).
/// </summary>
public sealed record Freshness
{
    /// <summary><c>etag:</c> (or the <c>fresh_when</c> object): the weak validator.</summary>
    public string? Etag { get; init; }

    /// <summary><c>strong_etag:</c></summary>
    public string? StrongEtag { get; init; }

    /// <summary><c>last_modified:</c> (or the object's <c>updated_at</c>).</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary><c>public:</c></summary>
    public bool Public { get; init; }

    /// <summary>
    /// The action template's digest (<c>ActionView::Digestor</c>), which
    /// <c>EtagWithTemplateDigest</c> adds whenever the action has a template.
    /// </summary>
    public string? Template { get; init; }
}

// Conditional GET (ActionController::ConditionalGet) and the response's cache headers.
public abstract partial class Controller
{
    /// <summary><c>response.cache_control</c></summary>
    public CacheControl CacheControl { get; } = new();

    /// <summary>
    /// <c>fresh_when</c>: sets <c>ETag</c> (the validator with the etaggers', digested) and
    /// <c>Last-Modified</c>, then answers <c>head :not_modified</c> when the request is fresh.
    /// True when it did.
    /// </summary>
    public bool FreshWhen(Freshness freshness)
    {
        ArgumentNullException.ThrowIfNull(freshness);
        CacheControl.NoStore = false;
        if (freshness.StrongEtag is { } strong)
        {
            Headers["ETag"] = $"\"{EtagDigest(CombineEtags(strong, freshness))}\"";
        }
        else if (freshness.Etag is not null || freshness.Template is not null)
        {
            Headers["ETag"] = $"W/\"{EtagDigest(CombineEtags(freshness.Etag, freshness))}\"";
        }
        if (freshness.LastModified is { } lastModified)
        {
            Headers["Last-Modified"] = HttpDate(lastModified);
        }
        if (freshness.Public)
        {
            CacheControl.Public = true;
        }
        if (IsFresh())
        {
            Head(304);
            return true;
        }
        return false;
    }

    /// <summary><c>stale?</c>: <see cref="FreshWhen"/>, true when the action should render.</summary>
    public bool IsStale(Freshness freshness)
    {
        FreshWhen(freshness);
        return !IsFresh();
    }

    /// <summary>
    /// <c>expires_in seconds, public:, must_revalidate:, stale_while_revalidate:, stale_if_error:,
    /// immutable:</c>, which also sets the <c>Date</c> header.
    /// </summary>
    public void ExpiresIn(
        TimeSpan maxAge,
        bool isPublic = false,
        bool mustRevalidate = false,
        TimeSpan? staleWhileRevalidate = null,
        TimeSpan? staleIfError = null,
        bool immutable = false)
    {
        CacheControl.NoStore = false;
        CacheControl.MaxAge = (long)maxAge.TotalSeconds;
        CacheControl.Public = isPublic;
        CacheControl.MustRevalidate = mustRevalidate;
        CacheControl.StaleWhileRevalidate = staleWhileRevalidate is { } swr ? (long)swr.TotalSeconds : null;
        CacheControl.StaleIfError = staleIfError is { } sie ? (long)sie.TotalSeconds : null;
        CacheControl.Immutable = immutable;
        CacheControl.Extras.Clear();
        if (!Headers.Contains("Date"))
        {
            Headers["Date"] = HttpDate(Now);
        }
    }

    /// <summary><c>expires_now</c>: <c>no-cache</c>.</summary>
    public void ExpiresNow()
    {
        CacheControl.Clear();
        CacheControl.NoCache = true;
    }

    /// <summary><c>no_store</c></summary>
    public void NoStore()
    {
        CacheControl.Clear();
        CacheControl.NoStore = true;
    }

    /// <summary>
    /// <c>http_cache_forever(public:)</c>: cached for 100 years; true when the action should render
    /// (the request isn't already fresh).
    /// </summary>
    public bool HttpCacheForever(bool isPublic = false)
    {
        // 100.years: 100 Julian-Gregorian years of 365.2425 days.
        ExpiresIn(TimeSpan.FromSeconds(3_155_695_200), isPublic, immutable: true);
        return IsStale(new Freshness
        {
            Etag = RequestUrl.FullPath,
            LastModified = new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Public = isPublic,
        });
    }

    /// <summary>
    /// <c>request.fresh?(response)</c> with <c>strict_freshness</c> (<c>load_defaults 8.0</c>):
    /// an <c>If-None-Match</c> naming the ETag (or <c>*</c>), or else an
    /// <c>If-Modified-Since</c> no earlier than <c>Last-Modified</c>.
    /// </summary>
    public bool IsFresh()
    {
        if (Header("If-None-Match") is { } ifNoneMatch)
        {
            var etag = Headers["ETag"];
            if (etag is null)
            {
                return false;
            }
            var validators = ifNoneMatch.Split(',').Select(validator => validator.Trim());
            return validators.Any(validator => validator == etag || validator == "*");
        }
        if (Header("If-Modified-Since") is { } ifModifiedSince && ParseHttpDate(ifModifiedSince) is { } since)
        {
            return Headers["Last-Modified"] is { } lastModified && ParseHttpDate(lastModified) is { } modified && since >= modified;
        }
        return false;
    }

    // combine_etags: [validator, *etaggers].compact as one cache key. ApplicationController's
    // etaggers, in the order they're declared: EtagWithTemplateDigest, EtagWithFlash, then
    // turbo-rails' frame etag.
    string CombineEtags(string? validator, Freshness freshness)
    {
        var parts = new List<string>();
        if (validator is not null)
        {
            parts.Add(validator);
        }
        if (freshness.Template is not null)
        {
            parts.Add(freshness.Template);
        }
        if (!Flash.IsEmpty)
        {
            parts.Add(CacheKeys.Expand(Flash.Keys.SelectMany(key => new[] { key, Flash[key] })));
        }
        if (Request.IsTurboFrameRequest)
        {
            parts.Add("frame");
        }
        return CacheKeys.Expand(parts);
    }

    /// <summary><c>ActiveSupport::Digest.hexdigest</c>: SHA-256 (<c>load_defaults 7.0</c>), cut to 32 hex digits.</summary>
    public static string EtagDigest(string cacheKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))[..32];

    /// <summary><c>Time#httpdate</c></summary>
    public static string HttpDate(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    // Time.rfc2822 / Time.httpdate, close enough for the dates browsers send back.
    static DateTimeOffset? ParseHttpDate(string text)
    {
        string[] formats = ["r", "ddd, d MMM yyyy HH:mm:ss 'GMT'", "ddd, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss 'GMT'"];
        return DateTimeOffset.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
    }
}

/// <summary>
/// <c>ActiveSupport::Cache.expand_cache_key</c>'s <c>retrieve_cache_key</c> for what the app
/// passes: strings, numbers, records (<c>cache_key_with_version</c>) and arrays of them.
/// </summary>
public static class CacheKeys
{
    /// <summary>
    /// A record's <c>cache_key_with_version</c>: <c>"&lt;collection&gt;/&lt;id&gt;-&lt;updated_at
    /// to_fs(:usec)&gt;"</c>, such as <c>users/1-20260302160000123456</c>.
    /// </summary>
    public static string Record(string collection, long id, DateTimeOffset updatedAt) =>
        $"{collection}/{id.ToString(CultureInfo.InvariantCulture)}-{RailsCompat.Formatting.TimeFormats.ToFsUsec(updatedAt)}";

    /// <summary>An array's key: its elements' keys joined with <c>/</c> (<c>Array#to_param</c>).</summary>
    public static string Expand(IEnumerable<string?> parts) => string.Join('/', parts.Select(part => part ?? ""));
}
