namespace Campfire.RailsCompat.UserAgent;

/// <summary>
/// <c>ApplicationPlatform</c> (reference/app/models/application_platform.rb) over platform_agent
/// 1.0.1, as <c>SetPlatform#platform</c> builds it from <c>request.user_agent</c>
/// (reference/app/controllers/concerns/set_platform.rb). The predicates that read the gem's
/// <c>browser</c> or <c>os</c> throw <see cref="GemRaisedException"/> where Ruby raises.
/// </summary>
public sealed class ApplicationPlatform
{
    readonly string userAgentString;

    public ApplicationPlatform(string? userAgent)
    {
        // `match?` works on `user_agent_string.to_s`, and UserAgent.parse treats nil like "".
        userAgentString = userAgent ?? "";
        UserAgent = ParsedUserAgent.Parse(userAgentString);
    }

    public ParsedUserAgent UserAgent { get; }

    bool Matches(string needle) => userAgentString.Contains(needle, StringComparison.Ordinal);

    public bool IsIos => Matches("iPhone") || Matches("iPad");

    public bool IsAndroid => Matches("Android");

    public bool IsMac => Matches("Macintosh");

    public bool IsChrome() => BrowserMatches("Chrome");

    public bool IsFirefox() => BrowserMatches("Firefox", "FxiOS");

    public bool IsSafari() => BrowserMatches("Safari");

    public bool IsEdge() => BrowserMatches("Edg");

    /// <summary>
    /// Apple Messages link previews claim to be both the Facebook and Twitter bots; Campfire
    /// doesn't show them "Unsupported browser".
    /// </summary>
    public bool IsAppleMessages
    {
        get
        {
            var downcased = RubyText.Downcase(userAgentString);
            return downcased.Contains("facebookexternalhit", StringComparison.Ordinal) &&
                downcased.Contains("twitterbot", StringComparison.Ordinal);
        }
    }

    public bool IsMobile => IsIos || IsAndroid;

    public bool IsDesktop => !IsMobile;

    public bool IsWindows() => OperatingSystem() == "Windows";

    /// <summary>The gem's <c>browser</c>, which platform_agent delegates.</summary>
    public string? Browser() => UserAgent.Browser();

    /// <summary><c>operating_system</c>: nil when the gem's <c>os</c> is nil.</summary>
    public string? OperatingSystem()
    {
        var platform = UserAgent.Platform() ?? "";
        (string Needle, string Name)[] named =
        [
            ("Android", "Android"), ("iPad", "iPad"), ("iPhone", "iPhone"),
            ("Macintosh", "macOS"), ("Windows", "Windows"), ("CrOS", "ChromeOS"),
        ];
        foreach (var (needle, name) in named)
        {
            if (platform.Contains(needle, StringComparison.Ordinal))
            {
                return name;
            }
        }

        var os = UserAgent.Os();
        return os is not null && os.Contains("Linux", StringComparison.Ordinal) ? "Linux" : os;
    }

    /// <summary><c>user_agent.browser.match?(/A|B/)</c>, which raises for a nil browser.</summary>
    bool BrowserMatches(params string[] names)
    {
        var browser = UserAgent.Browser() ?? throw new GemRaisedException("undefined method 'match?' for nil");
        return names.Any(name => browser.Contains(name, StringComparison.Ordinal));
    }
}
