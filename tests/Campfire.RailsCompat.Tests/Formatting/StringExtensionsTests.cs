using Campfire.RailsCompat.Formatting;

namespace Campfire.RailsCompat.Tests.Formatting;

public sealed class StringExtensionsTests
{
    [Fact]
    public void AllEmoji()
    {
        // reference/test/models/message_test.rb, "all emoji", on the plain text bodies.
        Assert.True(StringExtensions.AllEmoji("😄🤘"));
        Assert.False(StringExtensions.AllEmoji("Haha! 😄🤘"));
        Assert.False(StringExtensions.AllEmoji("🔥\nmultiple lines\n💯"));
        Assert.False(StringExtensions.AllEmoji("🔥 💯"));
    }
}
