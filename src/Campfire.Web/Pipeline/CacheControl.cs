using System.Globalization;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>response.cache_control</c>: the directives an action asks for (<c>expires_in</c>,
/// <c>fresh_when public:</c>, <c>no_store</c>...), and the header
/// <c>merge_and_normalize_cache_control!</c> (actionpack <c>http/cache.rb</c>) makes of them. The
/// app never sets a <c>Cache-Control</c> header by hand, so merging with one isn't modeled: a
/// header set directly is kept only when no directive was asked for.
/// </summary>
public sealed class CacheControl
{
    public bool NoStore { get; set; }
    public bool NoCache { get; set; }
    public long? MaxAge { get; set; }
    public bool Public { get; set; }
    public bool Private { get; set; }
    public bool MustRevalidate { get; set; }
    public bool MustUnderstand { get; set; }
    public long? StaleWhileRevalidate { get; set; }
    public long? StaleIfError { get; set; }
    public bool Immutable { get; set; }

    /// <summary>Other directives, written as given (<c>expires_in</c>'s unknown options).</summary>
    public List<string> Extras { get; } = [];

    /// <summary><c>cache_control.empty?</c></summary>
    public bool IsEmpty => !NoStore && !NoCache && MaxAge is null && !Public && !Private && !MustRevalidate
        && !MustUnderstand && StaleWhileRevalidate is null && StaleIfError is null && !Immutable && Extras.Count == 0;

    /// <summary><c>cache_control.replace({})</c></summary>
    public void Clear()
    {
        NoStore = NoCache = Public = Private = MustRevalidate = MustUnderstand = Immutable = false;
        MaxAge = StaleWhileRevalidate = StaleIfError = null;
        Extras.Clear();
    }

    /// <summary>The <c>Cache-Control</c> value for these directives, or null when there are none.</summary>
    public string? ToHeader()
    {
        if (IsEmpty)
        {
            return null;
        }
        var options = new List<string>();
        if (NoStore)
        {
            if (Private)
            {
                options.Add("private");
            }
            if (MustUnderstand)
            {
                options.Add("must-understand");
            }
            options.Add("no-store");
        }
        else if (NoCache)
        {
            if (Public)
            {
                options.Add("public");
            }
            options.Add("no-cache");
            options.AddRange(Extras);
        }
        else
        {
            if (MaxAge is { } maxAge)
            {
                options.Add($"max-age={maxAge.ToString(CultureInfo.InvariantCulture)}");
            }
            options.Add(Public ? "public" : "private");
            if (MustRevalidate)
            {
                options.Add("must-revalidate");
            }
            if (StaleWhileRevalidate is { } staleWhileRevalidate)
            {
                options.Add($"stale-while-revalidate={staleWhileRevalidate.ToString(CultureInfo.InvariantCulture)}");
            }
            if (StaleIfError is { } staleIfError)
            {
                options.Add($"stale-if-error={staleIfError.ToString(CultureInfo.InvariantCulture)}");
            }
            if (Immutable)
            {
                options.Add("immutable");
            }
            options.AddRange(Extras);
        }
        return string.Join(", ", options);
    }
}
