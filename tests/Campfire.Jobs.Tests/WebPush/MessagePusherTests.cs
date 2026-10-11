using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.Runner;
using Campfire.Jobs.Tests.RestrictedHttp;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Attachments;

namespace Campfire.Jobs.Tests.WebPush;

// reference/test/models/room/push_test.rb on the reference's fixtures: the message is created,
// Room::PushMessageJob runs, and each subscription it pushes to receives a request. The
// fixtures' keys aren't P-256 points, so every subscription is first given the reference test
// receiver's keys, and each push is decrypted with it.
public sealed class MessagePusherTests : IAsyncDisposable
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    readonly FixtureDatabase fixtures = new();
    readonly RecordingPushHandler service = new();
    readonly List<string> logged = [];
    readonly JobLog log;
    readonly WebPushPool pool;
    readonly MessagePusher pusher;
    readonly Mentions mentions;
    readonly long david, kevin, designers;

    public MessagePusherTests()
    {
        log = (level, message, _) =>
        {
            lock (logged)
            {
                logged.Add($"{level}: {message}");
            }
        };
        david = fixtures.Id("users", "David");
        kevin = fixtures.Id("users", "Kevin");
        designers = fixtures.Id("rooms", "Designers");
        fixtures.Execute("UPDATE push_subscriptions SET p256dh_key = @p256dh, auth_key = @auth", ("@p256dh", ReferenceVector.P256dh), ("@auth", ReferenceVector.Auth));

        var guard = new PrivateNetworkGuard(FakeResolver.Of(PushEndpointTests.PublicTestIp));
        var client = new WebPushClient(service, guard, new VapidIdentification(ReferenceVector.VapidPublicKey, ReferenceVector.VapidPrivateKey), TimeProvider.System);
        pool = new WebPushPool(client, WebPushPool.DestroySubscription(fixtures.Db, log), log);
        mentions = new Mentions(kevin);
        pusher = new MessagePusher(fixtures.Db, pool, _ => mentions, TimeProvider.System);
    }

    // `rooms(room).messages.create!` under `perform_enqueued_jobs only: Room::PushMessageJob`,
    // then `wait_for_web_push_delivery_pool_tasks(count)`.
    async Task CreateMessage(long roomId, string body, int expectedDeliveries)
    {
        await using var runner = new JobRunner(log);
        pusher.RegisterWith(runner);
        fixtures.Write(tx => MessageLifecycle.Create(tx, new DomainSeams(new RecordingSeams(), runner, new RecordingSeams()),
            roomId, david, "earth", $"<div>{body}</div>", body, DateTimeOffset.UtcNow));
        Assert.True(await runner.ShutdownAsync(TimeSpan.FromSeconds(10)));
        await WaitFor(() => pool.CompletedDeliveries >= expectedDeliveries, logged);
        Assert.Equal(expectedDeliveries, pool.CompletedDeliveries);
    }

    static async Task WaitFor(Func<bool> condition, List<string>? log = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timeout waiting for pool tasks to complete" + string.Join("\n", log ?? []));
            await Task.Delay(10, Cancellation);
        }
    }

    List<string> Endpoints() => [.. service.Requests.Select(request => request.Uri.AbsolutePath).Order()];

    static JsonNode Decrypt(RecordingPushHandler.Sent request)
    {
        var (_, plaintext) = ReferenceVector.Receiver.Decrypt(request.Body);
        return JsonNode.Parse(Encoding.UTF8.GetString(plaintext[..^2]))!;
    }

    [Fact]
    public async Task Deliver_new_message_to_other_room_users_with_push_subscriptions()
    {
        var hq = fixtures.Id("rooms", "HQ");
        var taskCount = fixtures.Read(PushSubscriptions.Count) - fixtures.Read(session => PushSubscriptions.CountForUser(session, david));

        await CreateMessage(hq, "This is from earth", (int)taskCount);

        Assert.Equal(["/fcm/send/456", "/fcm/send/567", "/fcm/send/789"], Endpoints());
        Assert.All(service.Requests, request => Assert.Equal(IPAddress.Parse(PushEndpointTests.PublicTestIp), request.PinnedAddress));
    }

    [Fact]
    public async Task Notifies_subscribed_users()
    {
        // Jason and JZ are involved in everything; David (the creator) and Kevin only in mentions.
        await CreateMessage(designers, "This is from earth", 2);
        Assert.Equal(["/fcm/send/456", "/fcm/send/567"], Endpoints());

        await CreateMessage(designers, $"Hey {mentions.Attachment}", 5);
        Assert.Equal(["/fcm/send/456", "/fcm/send/456", "/fcm/send/567", "/fcm/send/567", "/fcm/send/789"], Endpoints());
    }

    [Fact]
    public async Task Does_not_notify_for_connected_rooms()
    {
        fixtures.Execute("UPDATE memberships SET connected_at = @now, connections = 1 WHERE user_id = @user AND room_id = @room",
            ("@now", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff")), ("@user", kevin), ("@room", designers));

        await CreateMessage(designers, $"Hey {mentions.Attachment}", 2);

        Assert.DoesNotContain("/fcm/send/789", Endpoints());
    }

    [Fact]
    public async Task Does_not_notify_for_invisible_rooms()
    {
        fixtures.Execute("UPDATE memberships SET involvement = 'invisible' WHERE user_id = @user AND room_id = @room", ("@user", kevin), ("@room", designers));

        await CreateMessage(designers, $"Hey {mentions.Attachment}", 2);

        Assert.DoesNotContain("/fcm/send/789", Endpoints());
    }

    [Fact]
    public async Task Destroys_invalid_subscriptions()
    {
        fixtures.Execute("UPDATE memberships SET involvement = 'invisible' WHERE user_id = @user AND room_id = @room", ("@user", kevin), ("@room", designers));
        service.Status = HttpStatusCode.Gone;
        var before = fixtures.Read(PushSubscriptions.Count);

        await CreateMessage(designers, $"Hey {mentions.Attachment}", 2);
        await WaitFor(() => pool.CompletedInvalidations >= 2);

        Assert.Equal(before - 2, fixtures.Read(PushSubscriptions.Count));
        Assert.Equal(2, logged.Count(line => line.StartsWith("Information: Destroying push subscription: ", StringComparison.Ordinal)));
    }

    // A 404 is WebPush::InvalidSubscription, which WebPush::Pool#deliver doesn't rescue: logged,
    // and the subscription stays.
    [Fact]
    public async Task Keeps_subscriptions_the_push_service_does_not_know()
    {
        service.Status = HttpStatusCode.NotFound;
        var before = fixtures.Read(PushSubscriptions.Count);

        await CreateMessage(designers, "This is from earth", 2);
        await pool.ShutdownAsync();

        Assert.Equal(before, fixtures.Read(PushSubscriptions.Count));
        Assert.Equal(0, pool.CompletedInvalidations);
        Assert.Equal(2, logged.Count(line => line.StartsWith("Error: Error in WebPush::Pool.deliver: WebPush::InvalidSubscription host: fcm.googleapis.com, status: 404", StringComparison.Ordinal)));
    }

    // Subscription keys that aren't P-256 points raise an OpenSSL error, which destroys them too.
    [Fact]
    public async Task Destroys_subscriptions_whose_keys_are_not_points()
    {
        fixtures.Execute("UPDATE push_subscriptions SET p256dh_key = 'dGVzdF9rZXk'");
        var before = fixtures.Read(PushSubscriptions.Count);

        await CreateMessage(designers, "This is from earth", 2);
        await WaitFor(() => pool.CompletedInvalidations >= 2);

        Assert.Equal(before - 2, fixtures.Read(PushSubscriptions.Count));
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task Pushes_a_shared_rooms_message_titled_with_the_room()
    {
        await CreateMessage(designers, "This is from earth", 2);

        foreach (var request in service.Requests)
        {
            var message = Decrypt(request);
            Assert.Equal("Designers", (string?)message["title"]);
            Assert.Equal("David: This is from earth", (string?)message["options"]!["body"]);
            Assert.Equal(WebPushNotification.IconPath, (string?)message["options"]!["icon"]);
            Assert.Equal($"/rooms/{designers}", (string?)message["options"]!["data"]!["path"]);
        }
    }

    [Fact]
    public async Task Pushes_a_direct_message_titled_with_its_creator_and_badged_with_the_unread_count()
    {
        var direct = fixtures.Read(session => session.Query(
            "SELECT room_id FROM memberships WHERE user_id = @kevin AND room_id IN (SELECT room_id FROM memberships WHERE user_id = @david) AND room_id IN (SELECT id FROM rooms WHERE type = 'Rooms::Direct')",
            reader => reader.GetInt64(0), ("@kevin", kevin), ("@david", david)).Single());

        await CreateMessage(direct, "Psst", 1);

        var message = Decrypt(Assert.Single(service.Requests));
        Assert.Equal("David", (string?)message["title"]);
        Assert.Equal("Psst", (string?)message["options"]!["body"]);
        Assert.Equal(fixtures.Read(session => Memberships.CountUnreadForUser(session, kevin)), (long)message["options"]!["data"]!["badge"]!);
        Assert.True((long)message["options"]!["data"]!["badge"]! >= 1);
    }

    // Banning keeps the user's subscriptions, but a banned user is sent nothing (d9c3207).
    [Fact]
    public async Task Does_not_notify_banned_users()
    {
        Ban(kevin);

        await CreateMessage(designers, $"Hey {mentions.Attachment}", 2);
        await pool.ShutdownAsync();

        Assert.DoesNotContain("/fcm/send/789", Endpoints());
    }

    [Fact]
    public async Task Notifies_users_again_once_theyre_unbanned()
    {
        Ban(kevin);
        fixtures.Execute("UPDATE users SET status = 0 WHERE id = @id", ("@id", kevin));

        await CreateMessage(designers, $"Hey {mentions.Attachment}", 3);

        Assert.Contains("/fcm/send/789", Endpoints());
    }

    [Fact]
    public async Task Does_not_notify_banned_users_of_direct_messages()
    {
        Ban(kevin);

        await CreateMessage(DirectOfDavidAndKevin(), "Just between us", 0);
        await pool.ShutdownAsync();

        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task Notifies_active_users_mentioned()
    {
        await CreateMessage(designers, $"Hey {mentions.Attachment}", 3);

        Assert.Contains("/fcm/send/789", Endpoints());
    }

    // `push` builds no payload when nobody is to be notified (bbee420).
    [Fact]
    public void Pushes_nothing_and_builds_no_payload_without_subscriptions()
    {
        fixtures.Execute("DELETE FROM push_subscriptions");
        var room = fixtures.Read(session => Rooms.Find(session, designers))!;
        var message = fixtures.Write(tx => Messages.Create(tx.Session, designers, david, "unheard", DateTimeOffset.UtcNow));

        Assert.Null(fixtures.Read(session => pusher.Push(session, room, message)));
    }

    // reference/test/lib/web_push/pool_test.rb: the badges are counted once per batch of
    // subscriptions (819b389), each its subscriber's unread rooms.
    [Fact]
    public async Task Every_notification_carries_its_subscribers_unread_room_count_as_the_badge()
    {
        var (jason, pets, hq) = (fixtures.Id("users", "Jason"), fixtures.Id("rooms", "All Pets"), fixtures.Id("rooms", "HQ"));
        MarkUnread(jason, pets);
        MarkUnread(kevin, hq);
        MarkUnread(kevin, DirectOfDavidAndKevin());
        var subscriptions = fixtures.Read(session => new[] { david, jason, kevin }.SelectMany(user => PushSubscriptions.ForUser(session, user)).OrderBy(subscription => subscription.Id).ToList());
        var expected = fixtures.Read(session => subscriptions.ToDictionary(subscription => new Uri(subscription.Endpoint!).AbsolutePath, subscription => Memberships.CountUnreadForUser(session, subscription.UserId)));
        Assert.Equal([0L, 1L, 2L], expected.Values.Distinct().Order());

        fixtures.Read(session =>
        {
            pool.Queue(session, new PushPayload("Designers", "Hello", $"/rooms/{designers}"), subscriptions);
            return true;
        });
        await WaitFor(() => pool.CompletedDeliveries >= subscriptions.Count, logged);

        var badges = service.Requests.ToDictionary(request => request.Uri.AbsolutePath, request => (long)Decrypt(request)["options"]!["data"]!["badge"]!);
        Assert.Equal(expected.OrderBy(pair => pair.Key), badges.OrderBy(pair => pair.Key));
    }

    // reference/test/models/push/subscription_test.rb
    [Fact]
    public void The_badge_defaults_to_the_subscribers_unread_room_count()
    {
        MarkUnread(david, fixtures.Id("rooms", "All Pets"));
        MarkUnread(david, fixtures.Id("rooms", "HQ"));
        var subscription = fixtures.Read(session => PushSubscriptions.ForUser(session, david)[0]);

        var notification = fixtures.Read(session => WebPushNotification.For(session, subscription, new PushPayload("t", "b", "/")));

        Assert.Equal(fixtures.Read(session => Memberships.CountUnreadForUser(session, david)), notification.Badge);
        Assert.True(notification.Badge >= 2);
    }

    void Ban(long user) => fixtures.Execute("UPDATE users SET status = 2 WHERE id = @id", ("@id", user));

    void MarkUnread(long user, long room) =>
        fixtures.Execute("UPDATE memberships SET unread_at = @now WHERE user_id = @user AND room_id = @room", ("@now", "2026-03-02 16:00:00"), ("@user", user), ("@room", room));

    long DirectOfDavidAndKevin() => fixtures.Read(session => session.Query(
        "SELECT room_id FROM memberships WHERE user_id = @kevin AND room_id IN (SELECT room_id FROM memberships WHERE user_id = @david) AND room_id IN (SELECT id FROM rooms WHERE type = 'Rooms::Direct')",
        reader => reader.GetInt64(0), ("@kevin", kevin), ("@david", david)).Single());

    [Fact]
    public void Builds_the_payload_as_build_payload_does()
    {
        var room = fixtures.Read(session => Rooms.Find(session, designers))!;
        var creator = fixtures.Read(session => Users.Find(session, david))!;

        Assert.Equal(new PushPayload("Designers", "David: hi", $"/rooms/{designers}"), MessagePusher.BuildPayload(room, creator, "hi"));
        Assert.Equal(new PushPayload("David", "hi", $"/rooms/{designers}"), MessagePusher.BuildPayload(room with { Type = Campfire.Data.Records.RoomType.Direct }, creator, "hi"));
    }

    public async ValueTask DisposeAsync()
    {
        await pool.DisposeAsync();
        fixtures.Dispose();
    }

    /// <summary>
    /// <c>mention_attachment_for(:kevin)</c>'s attachment. Its SGID carries Kevin's GlobalID, which
    /// this resolver answers with Kevin; checking the signature is the web app's
    /// (<c>DatabaseAttachables</c>).
    /// </summary>
    sealed class Mentions(long kevin) : IAttachableResolver
    {
        readonly string sgid = RubyBase64.StrictEncode(Encoding.UTF8.GetBytes(
            $$$"""{"_rails":{"data":"gid://campfire/User/{{{kevin}}}","pur":"attachable"}}""")) + "--signature";

        MentionUser Kevin => new(kevin, "Kevin", "Kevin", sgid, $"/users/{kevin}", $"/users/{kevin}/avatar");

        public string Attachment => $"""<action-text-attachment sgid="{sgid}" content-type="application/vnd.campfire.mention" content="Kevin"></action-text-attachment>""";

        public SignedLookup LocateSigned(string candidate) => candidate == sgid ? new SignedLookup.User(Kevin) : SignedLookup.None;

        public MentionUser? FindGid(string gid, out GidLookupResult result)
        {
            result = gid == $"gid://campfire/User/{kevin}" ? GidLookupResult.OtherModel : GidLookupResult.NotFound;
            return result == GidLookupResult.OtherModel ? Kevin : null;
        }
    }
}
