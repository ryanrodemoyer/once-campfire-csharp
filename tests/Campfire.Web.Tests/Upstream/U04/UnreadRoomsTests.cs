using Campfire.Cable.Channels;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Upstream.U04;

/// <summary>
/// The unread-room notice fanned out from <c>Message::BroadcastUnreadRoomJob</c> (72648a8, 27065ef),
/// at reference edbc779: reference/test/controllers/messages_controller_test.rb's unread cases and
/// channels/unread_rooms_channel_test.rb, on the default parity seed (Designers stands in for the
/// watercooler; Kevin and Bender share a direct room).
/// </summary>
public sealed class UnreadRoomsTests : IDisposable
{
    const long designers = 654632876;
    const long kevinAndBender = 340026324;
    const long jason = 149087659;
    const long kevin = 712064548;
    const long bender = 394959859;
    const long jz = 773523953;
    const string davidSession = "AxJs94fteQ5Autv2VrKsH68c";

    readonly MessagesApp messages = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    public void Dispose() => messages.Dispose();

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_message_leaves_the_unread_fanout_to_a_job()
    {
        var response = await Post("message[body]=New+one&message[client_message_id]=999");

        Assert.Equal(200, response.Status);
        Assert.DoesNotContain(messages.Seams.Broadcasts, broadcast => broadcast.Stream.EndsWith("_unreads", StringComparison.Ordinal));
        var last = (long)messages.Scalar("SELECT MAX(id) FROM messages")!;
        Assert.Contains(new BroadcastUnreadRoomJob(last), messages.Seams.Jobs);
    }

    [Fact]
    public async Task The_unread_fanout_skips_a_member_who_has_opened_the_room_since()
    {
        await Post("message[body]=New+one&message[client_message_id]=999");
        Assert.NotNull(messages.Scalar($"SELECT unread_at FROM memberships WHERE room_id = {designers} AND user_id = {jason}") as string);

        // Jason opens the room, which reads it, and moves on before the job runs.
        await messages.Database.WriteAsync(tx =>
        {
            var membership = Memberships.FindFor(tx.Session, jason, designers)!;
            membership = Memberships.Present(tx.Session, membership, messages.Now);
            return Memberships.Disconnected(tx.Session, membership, messages.Now);
        }, Cancellation);
        await messages.PerformEnqueuedJobsAsync();

        Assert.DoesNotContain(messages.Seams.Broadcasts, broadcast => broadcast.Stream == UnreadRoomsChannel.StreamNameFor(jason));
        Assert.Contains(messages.Seams.Broadcasts, broadcast => broadcast.Stream == UnreadRoomsChannel.StreamNameFor(jz));
    }

    [Fact]
    public async Task An_outsider_is_not_told_about_activity_in_a_direct_room()
    {
        await CreateAndFanOutAsync(kevinAndBender, kevin, "outsider");

        Assert.DoesNotContain(messages.Seams.Broadcasts, broadcast => broadcast.Stream == UnreadRoomsChannel.StreamNameFor(jz));
    }

    [Fact]
    public async Task A_member_is_told_about_activity_in_their_own_room_and_when_it_was()
    {
        await CreateAndFanOutAsync(kevinAndBender, bender, "member");

        var broadcast = Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream == UnreadRoomsChannel.StreamNameFor(kevin));
        Assert.Equal($$"""{"roomId":{{kevinAndBender}},"at":1772467200.0}""", broadcast.Payload);
    }

    // `ActiveSupport::JSON.encode(roomId:, at: created_at.to_f)` for these created_at values as read
    // back from SQLite, as the reference image wrote them (Ruby 4.0.7, json gem): the float
    // doesn't round to the second, so two messages in the same second are told apart.
    [Theory]
    [InlineData("2026-03-02 16:00:00", "1772467200.0")]
    [InlineData("2026-03-02 16:00:00.5", "1772467200.5")]
    [InlineData("2026-03-02 16:00:00.123456", "1772467200.123456")]
    [InlineData("2028-05-20 09:54:34.005", "1842429274.0049999")]
    [InlineData("2026-10-09 16:00:00.000001", "1791561600.000001")]
    [InlineData("2026-10-09 16:00:00.999999", "1791561600.999999")]
    public async Task The_notice_carries_the_messages_time_as_the_reference_encodes_it(string createdAt, string at)
    {
        var id = await messages.Database.WriteAsync(tx => Messages.Create(tx.Session, kevinAndBender, bender, "timed", messages.Now).Id, Cancellation);
        messages.Scalar($"UPDATE messages SET created_at = '{createdAt}' WHERE id = {id}");
        messages.Scalar($"UPDATE memberships SET unread_at = '{createdAt}' WHERE room_id = {kevinAndBender}");

        await messages.Database.ReadAsync(session =>
        {
            UnreadRoomsChannel.BroadcastUnread(messages.Seams, session, Messages.Find(session, id)!, messages.Now);
            return true;
        }, Cancellation);

        Assert.All(messages.Seams.Broadcasts, broadcast => Assert.Equal($$"""{"roomId":{{kevinAndBender}},"at":{{at}}}""", broadcast.Payload));
        Assert.Equal(2, messages.Seams.Broadcasts.Count);
    }

    Task<Response> Post(string form) =>
        messages.SendAsync("POST", $"/rooms/{designers}/messages.turbo_stream", messages.SignedIn(davidSession), form);

    // `direct.messages.create!(...).broadcast_create` under `perform_enqueued_jobs only:
    // Message::BroadcastUnreadRoomJob`.
    async Task CreateAndFanOutAsync(long roomId, long creatorId, string clientMessageId)
    {
        var message = await messages.Database.WriteAsync(tx =>
            Campfire.Data.Lifecycle.MessageLifecycle.Create(tx, messages.Seams.Seams, roomId, creatorId, clientMessageId, "<p>Private</p>", "Private", messages.Now), Cancellation);
        messages.Seams.Clear();
        messages.Seams.Enqueue(new BroadcastUnreadRoomJob(message.Id));
        await messages.PerformEnqueuedJobsAsync();
    }
}
