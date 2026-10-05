using Campfire.RailsCompat.UserAgent;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.UserAgent;

// vectors/campfire_user_agents.json: what the useragent gem, ApplicationPlatform and allow_browser
// answer for each User-Agent in reference-tools/campfire/user_agents.rb.
public sealed class UserAgentVectorTests
{
    public static TheoryData<UserAgentCase> UserAgents() => CampfireVectors.UserAgents();

    public static TheoryData<UserAgentVersionCase> Versions() => CampfireVectors.UserAgentVersions();

    public static TheoryData<UserAgentComparisonCase> Comparisons() => CampfireVectors.UserAgentComparisons();

    [Theory]
    [MemberData(nameof(UserAgents))]
    public void ParsesLikeTheGem(UserAgentCase vector)
    {
        var agent = ParsedUserAgent.Parse(vector.Ua);

        Assert.Equal(vector.Browser, agent.Browser());
        AssertRuby(vector.Version, () => agent.Version()?.Text);
        AssertRuby(vector.Platform, agent.Platform);
        AssertRuby(vector.Os, agent.Os);
        Assert.Equal(vector.Bot, agent.IsBot);
        AssertRuby(vector.Mobile, agent.IsMobile);
    }

    [Theory]
    [MemberData(nameof(UserAgents))]
    public void ClassifiesLikeApplicationPlatform(UserAgentCase vector)
    {
        var expected = vector.ApplicationPlatform;
        var platform = new ApplicationPlatform(vector.Ua);

        Assert.Equal(expected.Ios, platform.IsIos);
        Assert.Equal(expected.Android, platform.IsAndroid);
        Assert.Equal(expected.Mac, platform.IsMac);
        AssertRuby(expected.Chrome, platform.IsChrome);
        AssertRuby(expected.Firefox, platform.IsFirefox);
        AssertRuby(expected.Safari, platform.IsSafari);
        AssertRuby(expected.Edge, platform.IsEdge);
        Assert.Equal(expected.AppleMessages, platform.IsAppleMessages);
        Assert.Equal(expected.Mobile, platform.IsMobile);
        Assert.Equal(expected.Desktop, platform.IsDesktop);
        AssertRuby(expected.Windows, platform.IsWindows);
        AssertRuby(expected.OperatingSystem, platform.OperatingSystem);
        Assert.Equal(expected.Browser, platform.Browser());
    }

    [Theory]
    [MemberData(nameof(UserAgents))]
    public void BlocksLikeAllowBrowser(UserAgentCase vector) =>
        AssertRuby(vector.Blocked, () => AllowBrowser.IsBlocked(vector.Ua));

    [Theory]
    [MemberData(nameof(Versions))]
    public void VersionsMatchTheGem(UserAgentVersionCase vector)
    {
        var version = new UserAgentVersion(vector.Text);

        Assert.Equal(vector.Nil, version.IsNil);
        Assert.Equal(vector.ToA, version.ToA().Select(segment => segment.ToString()));
    }

    [Theory]
    [MemberData(nameof(Comparisons))]
    public void ComparesLikeTheGem(UserAgentComparisonCase vector)
    {
        var (a, b) = (new UserAgentVersion(vector.A), new UserAgentVersion(vector.B));

        Assert.Equal(vector.Cmp, a.RubyCompare(b));
        Assert.Equal(vector.Lt, a < b);
        Assert.Equal(vector.Eq, a.Equals(b));
    }

    static void AssertRuby<T>(RubyResult<T> expected, Func<T> actual)
    {
        if (expected.Raised)
        {
            Assert.Throws<GemRaisedException>(() => actual());
        }
        else
        {
            Assert.Equal(expected.Value, actual());
        }
    }
}
