using System.Text.Json.Nodes;
using Campfire.Cable.Channels;
using Campfire.Cable.Server;
using Campfire.Cable.Turbo;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Signing;

namespace Campfire.Cable.Tests.Revocation;

/// <summary>
/// Revocation for every channel: subscribe, revoke (remove the membership, deactivate or ban),
/// attempt a delivery, reconnect and resubscribe (reference/app/models/membership.rb,
/// reference/app/models/user.rb, reference/app/models/user/bannable.rb; the rust port's
/// channels/tests/revocation_test.rs).
/// </summary>
public class RevocationTests
{
    public enum Revoke
    {
        Membership,
        Deactivation,
        Ban,
    }

    const string closeFrame = "close Some((1000, \"\"))";

    static readonly string[] Channels =
    [
        "ApplicationCable::Channel",
        "HeartbeatChannel",
        "PresenceChannel",
        "ReadRoomsChannel",
        "RoomChannel",
        "RoomMessagesChannel",
        "TypingNotificationsChannel",
        "UnreadRoomsChannel",
        "Turbo::StreamsChannel",
    ];

    public static TheoryData<string, Revoke> EveryChannel()
    {
        var data = new TheoryData<string, Revoke>();
        foreach (var channel in Channels)
        {
            foreach (var revoke in Enum.GetValues<Revoke>())
            {
                data.Add(channel, revoke);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryChannel))]
    public async Task Subscribe_revoke_deliver_reconnect(string channel, Revoke revoke)
    {
        await using var harness = await RevocationHarness.OpenAsync();
        var (kevin, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", kevin.Id, [kevin.Id]);
        var subscription = SubscriptionFor(channel, harness, kevin, designers);

        // Subscribe: a publication reaches the subscriber.
        using var client = await harness.ConnectAsync(cookie);
        await client.SubscribeAsync(subscription.Identifier);
        Assert.Equal(Confirm(subscription.Identifier), await client.NextAsync());
        if (subscription.Broadcasting is { } broadcasting)
        {
            Assert.Equal(1, Publish(harness, broadcasting, "before"));
            Assert.Equal(Delivery(subscription.Identifier, "before"), await client.NextAsync());
        }
        await client.AssertSilentAsync();

        // Revoke, then publish at once: the connection is told to go, and nothing follows.
        await RevokeAsync(harness, revoke, kevin.Id, designers.Id);
        if (subscription.Broadcasting is { } during)
        {
            Publish(harness, during, "during");
        }
        Assert.Equal(Closing(RemoteDisconnect(reconnect: revoke == Revoke.Membership)), await client.CollectAsync());
        if (subscription.Broadcasting is { } after)
        {
            await EventuallyAsync(() => Publish(harness, after, "after") == 0);
        }

        // Reconnect and resubscribe.
        using var again = await harness.App.ConnectAsync(cookie);
        if (revoke != Revoke.Membership)
        {
            Assert.Equal(Closing(Unauthorized), await again.CollectAsync());
            return;
        }
        Assert.Equal(CableProtocol.Welcome(), await again.NextAsync());
        await again.SubscribeAsync(subscription.Identifier);
        if (subscription.CarriesRoom)
        {
            Assert.Equal(CableProtocol.Rejection(subscription.Identifier), await again.NextAsync());
            Assert.Equal(0, Publish(harness, subscription.Broadcasting!, "again"));
        }
        else
        {
            Assert.Equal(Confirm(subscription.Identifier), await again.NextAsync());
            if (subscription.Broadcasting is { } kept)
            {
                Assert.Equal(1, Publish(harness, kept, "again"));
                Assert.Equal(Delivery(subscription.Identifier, "again"), await again.NextAsync());
            }
        }
        await again.AssertSilentAsync();
    }

    [Fact]
    public async Task Removing_a_membership_keeps_the_rooms_the_user_still_has()
    {
        await using var harness = await RevocationHarness.OpenAsync();
        var (kevin, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", kevin.Id, [kevin.Id]);
        var lounge = await harness.RoomAsync(RoomType.Closed, "Lounge", kevin.Id, [kevin.Id]);
        using var client = await harness.ConnectAsync(cookie);

        await RevokeAsync(harness, Revoke.Membership, kevin.Id, designers.Id);
        Assert.Equal(Closing(RemoteDisconnect(reconnect: true)), await client.CollectAsync());

        using var again = await harness.ConnectAsync(cookie);
        foreach (var identifier in new[] { RoomIdentifier("RoomChannel", lounge.Id), Signed("RoomMessagesChannel", harness, RoomSubscription.GidParam(lounge), "messages") })
        {
            await again.SubscribeAsync(identifier);
            Assert.Equal(Confirm(identifier), await again.NextAsync());
        }
        // The stock channel doesn't serve the lost room's messages either.
        var stock = Signed(TurboStreams.ClassName, harness, RoomSubscription.GidParam(designers), "messages");
        await again.SubscribeAsync(stock);
        Assert.Equal(CableProtocol.Rejection(stock), await again.NextAsync());

        // Unread fanout is authorized per publication: it goes to the room's members at the time.
        var unreads = Identifier("UnreadRoomsChannel");
        await again.SubscribeAsync(unreads);
        Assert.Equal(Confirm(unreads), await again.NextAsync());
        await harness.Database.ReadAsync(session =>
        {
            UnreadRoomsChannel.BroadcastUnread(harness.Server, session, designers.Id);
            UnreadRoomsChannel.BroadcastUnread(harness.Server, session, lounge.Id);
            return true;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(CableProtocol.Message(CableProtocol.EncodeString(unreads), RailsJson.Encode(new JsonObject { ["roomId"] = lounge.Id })), await again.NextAsync());
        await again.AssertSilentAsync();
    }

    [Theory]
    [InlineData(Revoke.Membership)]
    [InlineData(Revoke.Deactivation)]
    [InlineData(Revoke.Ban)]
    public async Task Only_the_revoked_users_connections_are_closed(Revoke revoke)
    {
        await using var harness = await RevocationHarness.OpenAsync();
        var (kevin, kevinCookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var (jz, jzCookie) = await harness.SignInAsync("JZ", "jz@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", kevin.Id, [kevin.Id, jz.Id]);
        var room = RoomIdentifier("RoomChannel", designers.Id);
        using var first = await harness.ConnectAsync(kevinCookie);
        using var second = await harness.ConnectAsync(kevinCookie);
        using var other = await harness.ConnectAsync(jzCookie);
        foreach (var client in new[] { first, second, other })
        {
            await client.SubscribeAsync(room);
            Assert.Equal(Confirm(room), await client.NextAsync());
        }

        await RevokeAsync(harness, revoke, kevin.Id, designers.Id);
        var broadcasting = ChannelNaming.BroadcastingFor("RoomChannel", [RoomSubscription.GidParam(designers)]);
        Publish(harness, broadcasting, "still here");
        var closed = Closing(RemoteDisconnect(reconnect: revoke == Revoke.Membership));
        Assert.Equal(closed, await first.CollectAsync());
        Assert.Equal(closed, await second.CollectAsync());
        Assert.Equal(Delivery(room, "still here"), await other.NextAsync());
        await EventuallyAsync(() => Publish(harness, broadcasting, "jz only") == 1);
        Assert.Equal(Delivery(room, "jz only"), await other.NextAsync());
        await other.AssertSilentAsync();
    }

    [Theory]
    [InlineData(Revoke.Deactivation)]
    [InlineData(Revoke.Ban)]
    public async Task A_connection_opened_while_the_revocation_is_in_its_transaction_is_refused(Revoke revoke)
    {
        await using var harness = await RevocationHarness.OpenAsync();
        var (kevin, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", kevin.Id, [kevin.Id]);
        using var entered = new SemaphoreSlim(0);
        using var commit = new SemaphoreSlim(0);

        // The revocation disconnects first, then holds its transaction open: its session is
        // still the last commit a reader sees.
        var revoking = harness.Database.WriteAsync(tx =>
        {
            var user = Users.Find(tx.Session, kevin.Id)!;
            if (revoke == Revoke.Ban)
            {
                UserLifecycle.Ban(tx, harness.Seams, user, RevocationHarness.Now);
            }
            else
            {
                UserLifecycle.Deactivate(tx, harness.Seams, user, RevocationHarness.Now);
            }
            entered.Release();
            commit.Wait(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await entered.WaitAsync(TestContext.Current.CancellationToken);

        using var client = await harness.App.ConnectAsync(cookie);
        try
        {
            // Without the guard this is a welcome, and the connection outlives the revocation.
            await client.AssertSilentAsync();
        }
        finally
        {
            commit.Release();
        }
        await revoking;
        Assert.Equal(Closing(Unauthorized), await client.CollectAsync());
        Assert.Equal(0, harness.Guard.PendingCount);

        Assert.Equal(0, Publish(harness, ChannelNaming.BroadcastingFor("RoomChannel", [RoomSubscription.GidParam(designers)]), "after"));
    }

    [Fact]
    public async Task A_revocation_that_rolls_back_still_disconnects_and_lets_the_user_back_in()
    {
        await using var harness = await RevocationHarness.OpenAsync();
        var (kevin, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        using var client = await harness.ConnectAsync(cookie);

        // Like Rails, the disconnect isn't transactional.
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Database.WriteAsync(tx =>
        {
            UserLifecycle.Deactivate(tx, harness.Seams, Users.Find(tx.Session, kevin.Id)!, RevocationHarness.Now);
            throw new InvalidOperationException("rolled back");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(Closing(RemoteDisconnect(reconnect: false)), await client.CollectAsync());

        using var again = await harness.ConnectAsync(cookie);
        Assert.Equal(0, harness.Guard.PendingCount);
        var heartbeat = Identifier("HeartbeatChannel");
        await again.SubscribeAsync(heartbeat);
        Assert.Equal(Confirm(heartbeat), await again.NextAsync());
    }

    [Fact]
    public async Task Connections_of_users_never_revoked_skip_the_guard()
    {
        await using var harness = await RevocationHarness.OpenAsync();
        var (_, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var (jz, _) = await harness.SignInAsync("JZ", "jz@example.com");
        await RevokeAsync(harness, Revoke.Deactivation, jz.Id, roomId: 0);
        Assert.Equal(1, harness.Guard.PendingCount);

        using var client = await harness.ConnectAsync(cookie);
        Assert.Equal(1, harness.Guard.PendingCount);
    }

    sealed record Subscription(string Identifier, string? Broadcasting, bool CarriesRoom);

    /// <summary>The channel's identifier for the room, and the broadcasting it streams from.</summary>
    static Subscription SubscriptionFor(string channel, RevocationHarness harness, User user, Room room)
    {
        var gid = RoomSubscription.GidParam(room);
        return channel switch
        {
            "ApplicationCable::Channel" or "HeartbeatChannel" => new(Identifier(channel), null, false),
            "ReadRoomsChannel" => new(Identifier(channel), ReadRoomsChannel.StreamNameFor(user.Id), false),
            "UnreadRoomsChannel" => new(Identifier(channel), UnreadRoomsChannel.StreamNameFor(user.Id), false),
            "RoomChannel" or "PresenceChannel" or "TypingNotificationsChannel" =>
                new(RoomIdentifier(channel, room.Id), ChannelNaming.BroadcastingFor(channel, [gid]), true),
            "RoomMessagesChannel" => new(Signed(channel, harness, gid, "messages"), $"{gid}:messages", true),
            // The sidebar's `turbo_stream_from Current.user, :rooms` (users/sidebars/show.html.erb).
            TurboStreams.ClassName => new(Signed(channel, harness, UserGid(user.Id), "rooms"), $"{UserGid(user.Id)}:rooms", false),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
        };
    }

    static async Task RevokeAsync(RevocationHarness harness, Revoke revoke, long userId, long roomId)
    {
        switch (revoke)
        {
            case Revoke.Membership:
                await MembershipLifecycle.RevokeFromAsync(harness.Database, harness.Seams, roomId, [userId]);
                break;
            case Revoke.Deactivation:
                await harness.Database.WriteAsync(tx => UserLifecycle.Deactivate(tx, harness.Seams, Users.Find(tx.Session, userId)!, RevocationHarness.Now), TestContext.Current.CancellationToken);
                break;
            case Revoke.Ban:
                await harness.Database.WriteAsync(tx => UserLifecycle.Ban(tx, harness.Seams, Users.Find(tx.Session, userId)!, RevocationHarness.Now), TestContext.Current.CancellationToken);
                break;
        }
    }

    static int Publish(RevocationHarness harness, string broadcasting, string word) =>
        harness.Server.Broadcast(broadcasting, new JsonObject { ["publication"] = word });

    static string Delivery(string identifier, string word) =>
        CableProtocol.Message(CableProtocol.EncodeString(identifier), RailsJson.Encode(new JsonObject { ["publication"] = word }));

    static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A connection's last events: the disconnect message, the close frame, then nothing.</summary>
    static string[] Closing(string disconnect) => [disconnect, closeFrame, "end"];

    static string RemoteDisconnect(bool reconnect) => CableProtocol.Disconnect(DisconnectReason.Remote, reconnect);

    static string Unauthorized => CableProtocol.Disconnect(DisconnectReason.Unauthorized, false);

    static string UserGid(long userId) => GlobalId.Create("User", userId).ToParam();

    static string Signed(string channel, RevocationHarness harness, params string[] streamables) =>
        RailsJson.Generate(new JsonObject { ["channel"] = channel, ["signed_stream_name"] = TurboStreamName.SignedStreamName(harness.Keys, streamables) });

    static string Identifier(string channel) => RailsJson.Generate(new JsonObject { ["channel"] = channel });

    static string RoomIdentifier(string channel, long roomId) => RailsJson.Generate(new JsonObject { ["channel"] = channel, ["room_id"] = roomId });

    static string Confirm(string identifier) => CableProtocol.Confirmation(identifier);
}
