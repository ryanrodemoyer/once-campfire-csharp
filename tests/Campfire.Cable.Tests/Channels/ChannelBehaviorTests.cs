using System.Text.Json.Nodes;
using Campfire.Cable.Channels;
using Campfire.Cable.Server;
using Campfire.Cable.Tests.Server;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Signing;

namespace Campfire.Cable.Tests.Channels;

/// <summary>
/// Ports of reference/test/channels and the channel cases the golden replay doesn't cover on its own.
/// </summary>
public class ChannelBehaviorTests
{
    [Fact]
    public async Task Heartbeat_and_base_channels_confirm()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (_, cookie) = await harness.SignInAsync("JZ", "jz@example.com");
        using var client = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());

        var heartbeat = Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" });
        var basis = Identifier(new JsonObject { ["channel"] = "ApplicationCable::Channel" });
        await client.SubscribeAsync(heartbeat);
        await client.SubscribeAsync(basis);
        Assert.Equal(Confirm(heartbeat), await client.NextAsync());
        Assert.Equal(Confirm(basis), await client.NextAsync());
        await client.AssertSilentAsync();
    }

    [Fact]
    public async Task Room_channel_streams_for_member_rooms_only()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (kevin, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var (other, _) = await harness.SignInAsync("Other", "other@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", kevin.Id, [kevin.Id]);
        var watercooler = await harness.RoomAsync(RoomType.Open, "Watercooler", other.Id, [other.Id]);
        using var client = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());

        var member = RoomIdentifier("RoomChannel", designers.Id);
        var byString = RoomIdentifier("RoomChannel", designers.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await client.SubscribeAsync(member);
        Assert.Equal(Confirm(member), await client.NextAsync());
        await client.SubscribeAsync(RoomIdentifier("RoomChannel", watercooler.Id));
        Assert.Equal(Reject(RoomIdentifier("RoomChannel", watercooler.Id)), await client.NextAsync());
        await client.SubscribeAsync(RoomIdentifier("RoomChannel", -1));
        Assert.Equal(Reject(RoomIdentifier("RoomChannel", -1)), await client.NextAsync());
        var missing = Identifier(new JsonObject { ["channel"] = "RoomChannel" });
        await client.SubscribeAsync(missing);
        Assert.Equal(Reject(missing), await client.NextAsync());
        await client.SubscribeAsync(byString);
        Assert.Equal(Confirm(byString), await client.NextAsync());

        var hello = RailsJson.Encode(new JsonObject { ["hello"] = 1 });
        harness.App.Server.BroadcastTo("RoomChannel", [RoomSubscription.GidParam(designers)], new JsonObject { ["hello"] = 1 });
        var received = await NextAsync(client, 2);
        var expected = new List<string> { Deliver(member, hello), Deliver(byString, hello) };
        expected.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, received);
        await client.AssertSilentAsync();
    }

    [Fact]
    public async Task Presence_subscribes_and_marks_the_membership_connected()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (david, cookie) = await harness.SignInAsync("David", "david@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", david.Id, [david.Id]);
        await harness.Database.WriteAsync(tx => tx.Session.Execute(
            """UPDATE "memberships" SET "unread_at" = @unread WHERE "user_id" = @user AND "room_id" = @room""",
            ("@unread", "2024-01-01 00:00:00"), ("@user", david.Id), ("@room", designers.Id)), TestContext.Current.CancellationToken);

        using var client = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());
        var reads = Identifier(new JsonObject { ["channel"] = "ReadRoomsChannel" });
        await client.SubscribeAsync(reads);
        Assert.Equal(Confirm(reads), await client.NextAsync());

        var presence = RoomIdentifier("PresenceChannel", designers.Id);
        await client.SubscribeAsync(presence);
        var read = RailsJson.Encode(new JsonObject { ["room_id"] = designers.Id });
        Assert.Equal(Sorted(Confirm(presence), Deliver(reads, read)), await NextAsync(client, 2));

        var connected = await MembershipAsync(harness, david.Id, designers.Id);
        Assert.True(connected.IsConnected(harness.Clock.UtcNow));
        Assert.Equal(1, connected.Connections);
        Assert.Null(connected.UnreadAt);

        await client.UnsubscribeAsync(presence);
        var gone = await WaitAsync(harness, david.Id, designers.Id, membership => !membership.IsConnected(harness.Clock.UtcNow));
        Assert.Equal((0L, (DateTimeOffset?)null), (gone.Connections, gone.ConnectedAt));
    }

    [Fact]
    public async Task Presence_counts_connections_and_refreshes_after_the_ttl()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (david, cookie) = await harness.SignInAsync("David", "david@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", david.Id, [david.Id]);
        var presence = RoomIdentifier("PresenceChannel", designers.Id);
        using var first = await harness.App.ConnectAsync(cookie);
        using var second = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await first.NextAsync());
        Assert.Equal(CableProtocol.Welcome(), await second.NextAsync());
        await first.SubscribeAsync(presence);
        await second.SubscribeAsync(presence);
        Assert.Equal(Confirm(presence), await first.NextAsync());
        Assert.Equal(Confirm(presence), await second.NextAsync());
        await WaitAsync(harness, david.Id, designers.Id, membership => membership.Connections == 2);

        harness.Clock.UtcNow += TimeSpan.FromSeconds(61);
        var stale = await MembershipAsync(harness, david.Id, designers.Id);
        Assert.False(stale.IsConnected(harness.Clock.UtcNow));
        await first.PerformAsync(presence, new JsonObject { ["action"] = "refresh" });
        var refreshed = await WaitAsync(harness, david.Id, designers.Id, membership => membership.IsConnected(harness.Clock.UtcNow));
        Assert.Equal(1, refreshed.Connections);

        await second.UnsubscribeAsync(presence);
        var cleared = await WaitAsync(harness, david.Id, designers.Id, membership => membership.ConnectedAt is null);
        Assert.Equal(0, cleared.Connections);
    }

    [Fact]
    public async Task Presence_rejects_rooms_the_user_is_not_in_without_touching_memberships()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (david, davidCookie) = await harness.SignInAsync("David", "david@example.com");
        var (kevin, _) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var direct = await harness.RoomAsync(RoomType.Direct, null, kevin.Id, [kevin.Id, david.Id]);
        var before = await MembershipAsync(harness, kevin.Id, direct.Id);

        using var client = await harness.App.ConnectAsync(davidCookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());
        // David is a member of the direct room; Kevin's other room is the one he isn't in.
        var closed = await harness.RoomAsync(RoomType.Closed, "Private", kevin.Id, [kevin.Id]);
        await client.SubscribeAsync(RoomIdentifier("PresenceChannel", closed.Id));
        Assert.Equal(Reject(RoomIdentifier("PresenceChannel", closed.Id)), await client.NextAsync());
        await client.SubscribeAsync(RoomIdentifier("PresenceChannel", -1));
        Assert.Equal(Reject(RoomIdentifier("PresenceChannel", -1)), await client.NextAsync());
        await client.AssertSilentAsync();
        Assert.Equal(before, await MembershipAsync(harness, kevin.Id, direct.Id));
    }

    [Fact]
    public async Task Unread_rooms_deliver_roomId_only_to_members_and_twice_after_subscribed_is_performed()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (kevin, kevinCookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var (_, outsiderCookie) = await harness.SignInAsync("JZ", "jz@example.com");
        var direct = await harness.RoomAsync(RoomType.Direct, null, kevin.Id, [kevin.Id]);
        var unreads = Identifier(new JsonObject { ["channel"] = "UnreadRoomsChannel" });

        using var outsider = await harness.App.ConnectAsync(outsiderCookie);
        using var member = await harness.App.ConnectAsync(kevinCookie);
        Assert.Equal(CableProtocol.Welcome(), await outsider.NextAsync());
        Assert.Equal(CableProtocol.Welcome(), await member.NextAsync());
        await outsider.SubscribeAsync(unreads);
        await member.SubscribeAsync(unreads);
        Assert.Equal(Confirm(unreads), await outsider.NextAsync());
        Assert.Equal(Confirm(unreads), await member.NextAsync());
        await member.PerformAsync(unreads, new JsonObject { ["action"] = "subscribed" });
        await member.AssertSilentAsync();

        await harness.Database.ReadAsync(session =>
        {
            UnreadRoomsChannel.BroadcastUnread(harness.App.Server, session, direct.Id);
            return true;
        }, TestContext.Current.CancellationToken);

        var payload = RailsJson.Encode(new JsonObject { ["roomId"] = direct.Id });
        var frame = Deliver(unreads, payload);
        Assert.Equal(Sorted(frame, frame), await NextAsync(member, 2));
        await member.AssertSilentAsync();
        await outsider.AssertSilentAsync();
    }

    [Fact]
    public async Task Read_rooms_stream_only_that_users_reads()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (jason, cookie) = await harness.SignInAsync("Jason", "jason@example.com");
        var (david, _) = await harness.SignInAsync("David", "david@example.com");
        using var client = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());
        var reads = Identifier(new JsonObject { ["channel"] = "ReadRoomsChannel" });
        await client.SubscribeAsync(reads);
        Assert.Equal(Confirm(reads), await client.NextAsync());

        ReadRoomsChannel.BroadcastRead(harness.App.Server, jason.Id, 7);
        ReadRoomsChannel.BroadcastRead(harness.App.Server, david.Id, 8);
        Assert.Equal(Deliver(reads, RailsJson.Encode(new JsonObject { ["room_id"] = 7 })), await client.NextAsync());
        await client.AssertSilentAsync();
    }

    [Fact]
    public async Task Typing_notifications_broadcast_start_and_stop_to_the_room()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (jz, jzCookie) = await harness.SignInAsync("JZ", "jz@example.com");
        var (kevin, kevinCookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", jz.Id, [jz.Id, kevin.Id]);
        var typing = RoomIdentifier("TypingNotificationsChannel", designers.Id);
        using var typist = await harness.App.ConnectAsync(jzCookie);
        using var reader = await harness.App.ConnectAsync(kevinCookie);
        Assert.Equal(CableProtocol.Welcome(), await typist.NextAsync());
        Assert.Equal(CableProtocol.Welcome(), await reader.NextAsync());
        await typist.SubscribeAsync(typing);
        await reader.SubscribeAsync(typing);
        Assert.Equal(Confirm(typing), await typist.NextAsync());
        Assert.Equal(Confirm(typing), await reader.NextAsync());

        await typist.PerformAsync(typing, new JsonObject { ["action"] = "start" });
        var start = Typing("start", jz);
        // Start both reads concurrently: delivery order between connections is non-deterministic.
        var readerStart = reader.NextAsync();
        var typistStart = typist.NextAsync();
        Assert.Equal(Deliver(typing, start), await readerStart);
        Assert.Equal(Deliver(typing, start), await typistStart);

        await typist.PerformAsync(typing, new JsonObject { ["action"] = "stop" });
        var stop = Typing("stop", jz);
        var readerStop = reader.NextAsync();
        var typistStop = typist.NextAsync();
        Assert.Equal(Deliver(typing, stop), await readerStop);
        Assert.Equal(Deliver(typing, stop), await typistStop);

        await typist.PerformAsync(typing, new JsonObject { ["action"] = "dance" });
        await reader.AssertSilentAsync();
        await typist.SubscribeAsync(Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" }));
        Assert.Equal(Confirm(Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" })), await typist.NextAsync());
    }

    [Fact]
    public async Task Typing_notifications_reach_the_room_however_the_channel_is_spelled()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (jz, jzCookie) = await harness.SignInAsync("JZ", "jz@example.com");
        var (kevin, kevinCookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", jz.Id, [jz.Id, kevin.Id]);
        var prefixed = RoomIdentifier("::TypingNotificationsChannel", designers.Id);
        var plain = RoomIdentifier("TypingNotificationsChannel", designers.Id);
        using var typist = await harness.App.ConnectAsync(jzCookie);
        using var reader = await harness.App.ConnectAsync(kevinCookie);
        Assert.Equal(CableProtocol.Welcome(), await typist.NextAsync());
        Assert.Equal(CableProtocol.Welcome(), await reader.NextAsync());
        await typist.SubscribeAsync(prefixed);
        await reader.SubscribeAsync(plain);
        Assert.Equal(Confirm(prefixed), await typist.NextAsync());
        Assert.Equal(Confirm(plain), await reader.NextAsync());

        await typist.PerformAsync(prefixed, new JsonObject { ["action"] = "start" });
        var start = Typing("start", jz);
        var readerStart = reader.NextAsync();
        var typistStart = typist.NextAsync();
        Assert.Equal(Deliver(plain, start), await readerStart);
        Assert.Equal(Deliver(prefixed, start), await typistStart);

        await reader.PerformAsync(plain, new JsonObject { ["action"] = "stop" });
        var stop = Typing("stop", kevin);
        var typistStop = typist.NextAsync();
        var readerStop = reader.NextAsync();
        Assert.Equal(Deliver(prefixed, stop), await typistStop);
        Assert.Equal(Deliver(plain, stop), await readerStop);
    }

    [Fact]
    public async Task Typing_on_a_failed_subscription_leaves_the_connection_up()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (jz, cookie) = await harness.SignInAsync("JZ", "jz@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", jz.Id, [jz.Id]);
        using var client = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());
        var typing = RoomIdentifier("TypingNotificationsChannel", designers.Id);

        await harness.Database.WriteAsync(tx => tx.Session.Execute("""ALTER TABLE "rooms" RENAME TO "rooms_away" """), TestContext.Current.CancellationToken);
        await client.SubscribeAsync(typing);
        var heartbeat = Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" });
        await client.SubscribeAsync(heartbeat);
        Assert.Equal(Confirm(heartbeat), await client.NextAsync());

        await harness.Database.WriteAsync(tx => tx.Session.Execute("""ALTER TABLE "rooms_away" RENAME TO "rooms" """), TestContext.Current.CancellationToken);
        await client.PerformAsync(typing, new JsonObject { ["action"] = "start" });
        var basis = Identifier(new JsonObject { ["channel"] = "ApplicationCable::Channel" });
        await client.SubscribeAsync(basis);
        Assert.Equal(Confirm(basis), await client.NextAsync());
    }

    [Fact]
    public async Task A_member_may_subscribe_to_a_rooms_message_stream_and_a_revoked_member_may_not()
    {
        await using var harness = await ChannelHarness.OpenAsync();
        var (kevin, cookie) = await harness.SignInAsync("Kevin", "kevin@example.com");
        var (bender, benderCookie) = await harness.SignInAsync("Bender", "bender@example.com");
        var designers = await harness.RoomAsync(RoomType.Open, "Designers", kevin.Id, [kevin.Id]);
        var hq = await harness.RoomAsync(RoomType.Closed, "HQ", bender.Id, [bender.Id]);
        var signed = TurboStreamName.SignedStreamName(harness.Keys, RoomSubscription.GidParam(designers), "messages");
        var channel = Messages(signed);

        using var member = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await member.NextAsync());
        await member.SubscribeAsync(channel);
        Assert.Equal(Confirm(channel), await member.NextAsync());

        var html = RailsJson.Encode(JsonValue.Create("""<turbo-stream action="remove" target="message_0001"></turbo-stream>"""));
        harness.App.Server.Broadcast($"{RoomSubscription.GidParam(designers)}:messages", html);
        Assert.Equal(Deliver(channel, html), await member.NextAsync());

        using var outsider = await harness.App.ConnectAsync(benderCookie);
        Assert.Equal(CableProtocol.Welcome(), await outsider.NextAsync());
        await outsider.SubscribeAsync(channel);
        Assert.Equal(Reject(channel), await outsider.NextAsync());
        var otherRoom = Messages(TurboStreamName.SignedStreamName(harness.Keys, RoomSubscription.GidParam(hq), "messages"));
        await member.SubscribeAsync(otherRoom);
        Assert.Equal(Reject(otherRoom), await member.NextAsync());

        var unsigned = Messages($"{RoomSubscription.GidParam(designers)}:messages");
        await member.SubscribeAsync(unsigned);
        Assert.Equal(Reject(unsigned), await member.NextAsync());
        var missing = Identifier(new JsonObject { ["channel"] = "RoomMessagesChannel" });
        await member.SubscribeAsync(missing);
        Assert.Equal(Reject(missing), await member.NextAsync());
        var rooms = Messages(TurboStreamName.SignedStreamName(harness.Keys, "rooms"));
        await member.SubscribeAsync(rooms);
        Assert.Equal(Reject(rooms), await member.NextAsync());

        var renamed = await harness.RoomAsync(RoomType.Open, "Renamed", kevin.Id, [kevin.Id]);
        var staleGid = GlobalId.Create(RoomType.Open.ClassName(), renamed.Id).ToParam();
        await harness.Database.WriteAsync(tx => Rooms.Update(tx.Session, Rooms.Find(tx.Session, renamed.Id)!, harness.Clock.UtcNow, type: RoomType.Closed), TestContext.Current.CancellationToken);
        var stale = Messages(TurboStreamName.SignedStreamName(harness.Keys, staleGid, "messages"));
        await member.SubscribeAsync(stale);
        Assert.Equal(Reject(stale), await member.NextAsync());

        var userGid = Messages(TurboStreamName.SignedStreamName(harness.Keys, GlobalId.Create("User", kevin.Id).ToParam(), "messages"));
        await member.SubscribeAsync(userGid);
        Assert.Equal(Reject(userGid), await member.NextAsync());

        var unknown = Messages(TurboStreamName.SignedStreamName(harness.Keys, GlobalId.Create("NotARealModel", 1).ToParam(), "messages"));
        await member.SubscribeAsync(unknown);
        var heartbeat = Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" });
        await member.SubscribeAsync(heartbeat);
        Assert.Equal(Confirm(heartbeat), await member.NextAsync());

        var seams = new DomainSeams(new NullBroadcaster(), new NullJobs(), new ServerRevoker(harness.App.Server));
        await MembershipLifecycle.RevokeFromAsync(harness.Database, seams, designers.Id, [kevin.Id]);
        Assert.Equal("""{"type":"disconnect","reason":"remote","reconnect":true}""", await member.NextAsync());

        using var again = await harness.App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await again.NextAsync());
        await again.SubscribeAsync(channel);
        Assert.Equal(Reject(channel), await again.NextAsync());
    }

    static string Typing(string action, User user) => RailsJson.Encode(new JsonObject
    {
        ["action"] = action,
        ["user"] = new JsonObject { ["id"] = user.Id, ["name"] = user.Name },
    });

    static string Messages(string signed) => Identifier(new JsonObject { ["channel"] = "RoomMessagesChannel", ["signed_stream_name"] = signed });

    static string Identifier(JsonObject value) => RailsJson.Generate(value);

    static string RoomIdentifier(string channel, long roomId) => Identifier(new JsonObject { ["channel"] = channel, ["room_id"] = roomId });

    static string RoomIdentifier(string channel, string roomId) => Identifier(new JsonObject { ["channel"] = channel, ["room_id"] = roomId });

    static string Confirm(string identifier) => CableProtocol.Confirmation(identifier);

    static string Reject(string identifier) => CableProtocol.Rejection(identifier);

    static string Deliver(string identifier, string encodedMessage) => CableProtocol.Message(CableProtocol.EncodeString(identifier), encodedMessage);

    static List<string> Sorted(params string[] frames)
    {
        var list = frames.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    static async Task<List<string>> NextAsync(CableClient client, int count)
    {
        var frames = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            frames.Add(await client.NextAsync());
        }

        frames.Sort(StringComparer.Ordinal);
        return frames;
    }

    static Task<Membership> MembershipAsync(ChannelHarness harness, long userId, long roomId) =>
        harness.Database.ReadAsync(session => Memberships.FindFor(session, userId, roomId)!, TestContext.Current.CancellationToken);

    static async Task<Membership> WaitAsync(ChannelHarness harness, long userId, long roomId, Func<Membership, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Membership? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await MembershipAsync(harness, userId, roomId);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"membership connections={last?.Connections} connected_at={last?.ConnectedAt}");
    }

    sealed class NullJobs : IJobQueue
    {
        public void Enqueue(Job job) => ArgumentNullException.ThrowIfNull(job);
    }

    sealed class NullBroadcaster : IBroadcaster
    {
        public void Broadcast(string stream, string payload)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(payload);
        }
    }

    sealed class ServerRevoker(CableServer<User> server) : IConnectionRevoker
    {
        public void Disconnect(long userId, bool reconnect) => server.Disconnect(SessionCookieAuthenticator.ConnectionIdentifier(userId), reconnect);
    }
}
