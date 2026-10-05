using Campfire.RailsCompat.Formatting;

namespace Campfire.RailsCompat.Tests.Formatting;

// The calls the reference app makes, with what the reference renders.
public sealed class TextHelpersTests
{
    [Fact]
    public void TruncatesOpengraphEmbedsLikeTheView()
    {
        // reference/app/views/action_text/attachables/_opengraph_embed.html.erb
        Assert.Equal(new string('a', 279) + "…", TextHelpers.Truncate(new string('a', 300), 280, "…"));
        Assert.Equal("short", TextHelpers.Truncate("short", 560, "…"));
    }

    [Fact]
    public void JoinsDirectRoomInitialsLikeTheSidebar()
    {
        // reference/app/views/users/sidebars/rooms/_direct.html.erb
        Assert.Equal("AB+CD", TextHelpers.ToSentence(["AB", "CD"], twoWordsConnector: "+"));
        Assert.Equal("A, B, and C", TextHelpers.ToSentence(["A", "B", "C"], twoWordsConnector: "+"));
    }
}
