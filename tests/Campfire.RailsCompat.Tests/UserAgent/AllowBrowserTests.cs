using Campfire.RailsCompat.UserAgent;

namespace Campfire.RailsCompat.Tests.UserAgent;

public sealed class AllowBrowserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \u3000")]
    public void AbsentUserAgentsAreAllowed(string? userAgent) => Assert.False(AllowBrowser.IsBlocked(userAgent));

    [Fact]
    public void BlankUserAgentsParseAsTheDefault()
    {
        var agent = ParsedUserAgent.Parse("  ");

        Assert.Equal("Mozilla", agent.Browser());
        Assert.Equal("4.0", agent.Version()?.Text);
    }
}
