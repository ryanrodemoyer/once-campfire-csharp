using System.Text.RegularExpressions;

namespace Campfire.RailsCompat.UserAgent;

/// <summary>
/// The useragent gem's regexps. Ruby's <c>\d</c> and <c>\s</c> are ASCII-only, so they are
/// spelled <c>[0-9]</c> and <c>[ \t\n\v\f\r]</c> here.
/// </summary>
static partial class GemPatterns
{
    const string space = @"[ \t\n\v\f\r]";

    /// <summary><c>UserAgent::MATCHER</c>.</summary>
    [GeneratedRegex(@"^['""]*([^/ \t\n\v\f\r]+)/?([^ \t\n\v\f\r,]*)(" + space + @"\(([^\)]*)\)|,gzip\(gfe\))?", RegexOptions.CultureInvariant)]
    public static partial Regex Product();

    [GeneratedRegex(@"(?:Intel|PPC) Mac OS X" + space + @"*([0-9_\.]+)?", RegexOptions.CultureInvariant)]
    public static partial Regex MacOsX();

    /// <summary><c>IOS_VERSION_REGEX</c>.</summary>
    [GeneratedRegex(@"CPU (?:iPhone |iPod )?OS ([0-9_]+) like Mac OS X", RegexOptions.CultureInvariant)]
    public static partial Regex IosVersion();

    [GeneratedRegex(@"CrOS" + space + @"([^ \t\n\v\f\r]+)" + space + @"([0-9]+(\.[0-9]+)*)", RegexOptions.CultureInvariant)]
    public static partial Regex ChromeOs();

    [GeneratedRegex(@"Windows NT [0-9\.]+|Windows Phone (OS )?[0-9\.]+", RegexOptions.CultureInvariant)]
    public static partial Regex WindowsOs();

    [GeneratedRegex(@"Trident.+rv:", RegexOptions.CultureInvariant)]
    public static partial Regex TridentRv();

    [GeneratedRegex(@"(MSIE" + space + @"|rv:)([0-9\.]+)", RegexOptions.CultureInvariant)]
    public static partial Regex IeVersion();

    [GeneratedRegex(@"Opera Mini/([0-9\.]+)", RegexOptions.CultureInvariant)]
    public static partial Regex OperaMiniVersion();

    [GeneratedRegex(@"iOS ([0-9\.]+)", RegexOptions.CultureInvariant)]
    public static partial Regex IosOsVersion();

    /// <summary><c>WEBKIT_VERSION_REGEXP</c>.</summary>
    [GeneratedRegex(@"\A(?<webkit>AppleWebKit)/(?<version>[0-9\.]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    public static partial Regex WebkitComment();

    /// <summary>iTunes on Windows: a comment the parser cut at the ")" closing "(Build 7601)".</summary>
    [GeneratedRegex(@"\(Build [0-9]{4}\z", RegexOptions.CultureInvariant)]
    public static partial Regex CutBuild();

    public static string? Capture(Regex regex, string text, int group)
    {
        var match = regex.Match(text);
        return match.Success && match.Groups[group].Success ? match.Groups[group].Value : null;
    }
}
