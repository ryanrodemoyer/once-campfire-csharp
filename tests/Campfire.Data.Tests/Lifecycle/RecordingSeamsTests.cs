using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Data.Tests.Lifecycle;

// The in-memory fakes the M, RT and I lanes test against.
public class RecordingSeamsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 3, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Records_every_seam_in_one_ordered_log()
    {
        var seams = new RecordingSeams();

        seams.Seams.Connections.Disconnect(7, reconnect: true);
        seams.Seams.Jobs.Enqueue(new PushMessageJob(1, 2));
        seams.Seams.Broadcaster.Broadcast("user_7_unreads", """{"roomId":1}""");

        Assert.Equal<SeamEvent>([new Disconnect(7, true), new Enqueued(new PushMessageJob(1, 2)), new Broadcast("user_7_unreads", """{"roomId":1}""")], seams.Events);
        Assert.Equal([new PushMessageJob(1, 2)], seams.Jobs);
        Assert.Equal([new Disconnect(7, true)], seams.Disconnects);
        Assert.Equal([new Broadcast("user_7_unreads", """{"roomId":1}""")], seams.Broadcasts);

        seams.Clear();
        Assert.Empty(seams.Events);
    }

    [Fact]
    public void Jobs_carry_their_active_job_class_and_record_ids()
    {
        Assert.Equal("Room::PushMessageJob", new PushMessageJob(1, 2).ClassName);
        Assert.Equal([1, 2], new PushMessageJob(1, 2).ArgumentIds);
        Assert.Equal("Bot::WebhookJob", new WebhookJob(3, 4).ClassName);
        Assert.Equal([3, 4], new WebhookJob(3, 4).ArgumentIds);
        Assert.Equal("RemoveBannedContentJob", new RemoveBannedContentJob(5).ClassName);
        Assert.Equal([5], new RemoveBannedContentJob(5).ArgumentIds);
    }

    // reference/test/models/room_test.rb-style: a new message pushes, and a member watching the
    // room isn't marked unread. The push job waits for the commit; a rolled-back message never
    // enqueues it.
    [Fact]
    public async Task A_message_enqueues_its_push_only_once_committed()
    {
        using var fixtures = new Fixtures.Database();
        var seams = new RecordingSeams();
        var room = Fixtures.Id("pets");
        var creator = Fixtures.Id("david");

        var enqueuedInside = -1;
        var message = await fixtures.Db.WriteAsync(transaction =>
        {
            var created = MessageLifecycle.Create(transaction, seams.Seams, room, creator, null, "<div>Hi</div>", "Hi", Now);
            enqueuedInside = seams.Jobs.Count;
            return created;
        }, Ct);

        Assert.Equal(0, enqueuedInside);
        Assert.Equal([new PushMessageJob(room, message.Id)], seams.Jobs);

        seams.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixtures.Db.WriteAsync(transaction =>
        {
            MessageLifecycle.Create(transaction, seams.Seams, room, creator, null, "<div>Gone</div>", "Gone", Now);
            throw new InvalidOperationException("rolled back");
        }, Ct));
        Assert.Empty(seams.Events);
    }

    // Like ActionCable's remote-connections broadcast, a disconnect isn't transactional: it has
    // already gone out when a later failure rolls the deactivation back.
    [Fact]
    public async Task A_disconnect_goes_out_even_if_the_transaction_rolls_back()
    {
        using var fixtures = new Fixtures.Database();
        var seams = new RecordingSeams();
        var kevin = await fixtures.Db.ReadAsync(session => Users.Find(session, Fixtures.Id("kevin"))!, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixtures.Db.WriteAsync(transaction =>
        {
            UserLifecycle.Deactivate(transaction, seams.Seams, kevin, Now);
            throw new InvalidOperationException("rolled back");
        }, Ct));

        Assert.Equal([new Disconnect(kevin.Id, false)], seams.Disconnects);
        Assert.True((await fixtures.Db.ReadAsync(session => Users.Find(session, kevin.Id)!, Ct)).IsActive);
    }

    [Fact]
    public async Task A_revoked_membership_resets_connections_after_its_commit()
    {
        using var fixtures = new Fixtures.Database();
        var seams = new RecordingSeams();
        var room = Fixtures.Id("designers");
        var jz = Fixtures.Id("jz");

        await MembershipLifecycle.RevokeFromAsync(fixtures.Db, seams.Seams, room, [jz]);

        Assert.Equal([new Disconnect(jz, true)], seams.Disconnects);
        Assert.Null(await fixtures.Db.ReadAsync(session => Memberships.FindFor(session, jz, room), Ct));
    }

    [Fact]
    public async Task A_new_user_joins_the_open_rooms_once_committed()
    {
        using var fixtures = new Fixtures.Database();

        var user = await fixtures.Db.WriteAsync(transaction => UserLifecycle.Create(transaction, "Nina", "nina@example.com", null, Now), Ct);

        var (open, joined) = await fixtures.Db.ReadAsync(session =>
            (Rooms.IdsOfType(session, RoomType.Open).Order().ToList(), Rooms.ForUser(session, user.Id).Select(r => r.Id).Order().ToList()), Ct);
        Assert.NotEmpty(open);
        Assert.Equal(open, joined);
    }
}
