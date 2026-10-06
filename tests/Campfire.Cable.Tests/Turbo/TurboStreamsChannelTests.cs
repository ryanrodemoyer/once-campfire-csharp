using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Cable.Tests.Server;
using Campfire.Cable.Turbo;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Signing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Cable.Tests.Turbo;

/// <summary>
/// <c>Turbo::StreamsChannel</c> with <c>RoomStreamsAreAuthorized</c>: signed names stream, and a
/// leaked room message name is rejected for a non-member (and for a member; this channel does
/// not check membership).
/// </summary>
public sealed class TurboStreamsChannelTests
{
    const string welcome = """{"type":"welcome"}""";

    static readonly KeyGenerator Keys = new("test-secret");

    static string Identifier(JsonObject value) => RailsJson.Generate(value);

    static string Confirm(string identifier) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"type":"confirm_subscription"}""";

    static string Reject(string identifier) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"type":"reject_subscription"}""";

    static string Message(string identifier, string message) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"message":{{message}}}""";

    static string Turbo(string? signed) => Identifier(signed is null
        ? new JsonObject { ["channel"] = TurboStreams.ClassName }
        : new JsonObject { ["channel"] = TurboStreams.ClassName, ["signed_stream_name"] = signed });

    [Fact]
    public void Guards_only_message_streams()
    {
        Assert.True(RoomStreams.IsGuarded("Z2lkOi8vY2FtcGZpcmUvUm9vbXM6Ok9wZW4vMQ:messages"));
        Assert.False(RoomStreams.IsGuarded("rooms"));
        Assert.False(RoomStreams.IsGuarded("Z2lk:rooms"));
        Assert.False(RoomStreams.IsGuarded("Z2lk:messages:more"));
        Assert.False(RoomStreams.IsGuarded(""));
        Assert.True(RoomStreams.IsGuarded(":messages"));
    }

    [Fact]
    public async Task A_signed_stream_name_confirms_and_delivers()
    {
        var signed = TurboStreamName.SignedStreamName(Keys, "rooms");
        var identifier = Turbo(signed);
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=outsider");
        Assert.Equal(welcome, await client.NextAsync());
        await client.SubscribeAsync(identifier);

        Assert.Equal(Confirm(identifier), await client.NextAsync());
        var payload = RailsJson.Encode(JsonValue.Create("<turbo-stream action=\"append\" target=\"rooms\"><template>x</template></turbo-stream>"));
        app.Server.Broadcast("rooms", payload);
        Assert.Equal(Message(identifier, payload), await client.NextAsync());
    }

    [Fact]
    public async Task Rejects_a_leaked_signed_room_stream_name_for_a_non_member()
    {
        var gid = GlobalId.Create("Rooms::Open", 7).ToParam();
        var signed = TurboStreamName.SignedStreamName(Keys, gid, "messages");
        var verified = TurboStreams.VerifiedStreamName(Keys, new JsonObject { ["signed_stream_name"] = signed });
        Assert.Equal($"{gid}:messages", verified);
        Assert.True(RoomStreams.IsGuarded(verified!));

        var identifier = Turbo(signed);
        await using var app = await StartAsync();

        // The name was harvested while someone was a member. It carries no user and no expiry.
        // Turbo::StreamsChannel turns it away; RoomMessagesChannel (RT03) is the only door.
        using var outsider = await app.ConnectAsync("session_token=outsider");
        Assert.Equal(welcome, await outsider.NextAsync());
        await outsider.SubscribeAsync(identifier);
        Assert.Equal(Reject(identifier), await outsider.NextAsync());
        app.Server.Broadcast(verified!, RailsJson.Encode(JsonValue.Create("<turbo-stream action=\"append\" target=\"messages\"><template>x</template></turbo-stream>")));
        await outsider.AssertSilentAsync();

        // The same name is rejected for a member. Membership is not what this channel checks.
        using var member = await app.ConnectAsync("session_token=member");
        Assert.Equal(welcome, await member.NextAsync());
        await member.SubscribeAsync(identifier);
        Assert.Equal(Reject(identifier), await member.NextAsync());
    }

    [Fact]
    public async Task A_forged_or_missing_signed_name_is_rejected()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=outsider");
        Assert.Equal(welcome, await client.NextAsync());

        var forged = Turbo("InJvb21zIg==--0000");
        await client.SubscribeAsync(forged);
        Assert.Equal(Reject(forged), await client.NextAsync());

        var missing = Turbo(null);
        await client.SubscribeAsync(missing);
        Assert.Equal(Reject(missing), await client.NextAsync());
    }

    static Task<CableTestApp<Viewer>> StartAsync() =>
        CableTestApp.StartAsync(CableServer.Builder(new CableConfig { AssumeSsl = false }, new CookieViewer())
            .Channel(TurboStreams.ClassName, () => new TurboStreamsChannel<Viewer>(Keys))
            .Build());

    sealed record Viewer(long Id);

    /// <summary>Two cookies, neither of which the channel consults. The outsider is not a member.</summary>
    sealed class CookieViewer : ICableAuthenticator<Viewer>
    {
        public ValueTask<CableIdentity<Viewer>?> ConnectAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            var id = request.Headers.Cookie.ToString() switch
            {
                "session_token=member" => 1L,
                "session_token=outsider" => 2L,
                _ => (long?)null,
            };
            return ValueTask.FromResult<CableIdentity<Viewer>?>(id is { } userId
                ? new CableIdentity<Viewer>(new Viewer(userId), $"gid://campfire/User/{userId}")
                : null);
        }
    }
}
