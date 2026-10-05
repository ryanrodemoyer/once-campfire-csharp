namespace Campfire.RailsCompat.UserAgent;

/// <summary>
/// <c>allow_browser</c> with Campfire's <c>AllowBrowser::VERSIONS</c>
/// (reference/app/controllers/concerns/allow_browser.rb), as
/// <c>ActionController::AllowBrowser::BrowserBlocker#blocked?</c> decides it. A blocked request
/// renders <c>sessions/incompatible_browser</c>.
/// </summary>
public static class AllowBrowser
{
    /// <summary>
    /// <c>{ safari: 17.2, chrome: 120, firefox: 121, opera: 104, ie: false }</c>, keyed by the
    /// gem's downcased browser name. A null minimum is
    /// <c>false</c>: every version is blocked.
    /// </summary>
    static readonly Dictionary<string, UserAgentVersion?> Versions = new(StringComparer.Ordinal)
    {
        ["safari"] = new("17.2"),
        ["chrome"] = new("120"),
        ["firefox"] = new("121"),
        ["opera"] = new("104"),
        ["ie"] = null,
    };

    /// <summary>
    /// <c>BrowserBlocker#blocked?</c> for a request's User-Agent header. Throws
    /// <see cref="GemRaisedException"/> where Rails raises (a versioned agent with a nil browser).
    /// </summary>
    public static bool IsBlocked(string? userAgent) =>
        RubyText.IsPresent(userAgent) && IsBlocked(ParsedUserAgent.Parse(userAgent));

    /// <summary><c>blocked?</c> for an already parsed, present User-Agent.</summary>
    public static bool IsBlocked(ParsedUserAgent agent)
    {
        if (agent.Version() is not { IsPresent: true } version)
        {
            return false;
        }

        var browser = agent.Browser() ?? throw new GemRaisedException("undefined method 'downcase' for nil");
        if (!Versions.TryGetValue(NormalizedBrowserName(browser), out var minimum))
        {
            return false;
        }
        var belowMinimum = minimum is null || version < minimum;
        return belowMinimum && !agent.IsBot;
    }

    /// <summary><c>normalized_browser_name</c>: the downcased browser, with "internet explorer" as <c>:ie</c>.</summary>
    static string NormalizedBrowserName(string browser)
    {
        var name = RubyText.Downcase(browser);
        return name == "internet explorer" ? "ie" : name;
    }
}
