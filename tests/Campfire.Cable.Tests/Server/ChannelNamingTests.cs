using Campfire.Cable.Server;
using Campfire.RailsCompat.GlobalId;

namespace Campfire.Cable.Tests.Server;

/// <summary>reference-rust/crates/cable/src/naming.rs's tests.</summary>
public sealed class ChannelNamingTests
{
    [Theory]
    [InlineData("RoomChannel", "room")]
    [InlineData("TypingNotificationsChannel", "typing_notifications")]
    [InlineData("Turbo::StreamsChannel", "turbo:streams")]
    [InlineData("HTMLChannel", "html")]
    [InlineData("HTMLParserChannel", "html_parser")]
    [InlineData("ApplicationCable::Channel", "application_cable:")]
    public void Channel_names(string className, string expected) =>
        Assert.Equal(expected, ChannelNaming.ChannelName(className));

    [Fact]
    public void Broadcastings()
    {
        var room = GlobalId.Create("Rooms::Open", 1).ToParam();
        Assert.Equal("Z2lkOi8vY2FtcGZpcmUvUm9vbXM6Ok9wZW4vMQ", room);
        Assert.Equal($"presence:{room}", ChannelNaming.BroadcastingFor("PresenceChannel", room));
    }

    [Fact]
    public void Connection_identifiers_are_user_global_ids() =>
        Assert.Equal("action_cable/gid://campfire/User/7", CableServer.InternalChannel(SessionCookieAuthenticator.ConnectionIdentifier(7)));
}
