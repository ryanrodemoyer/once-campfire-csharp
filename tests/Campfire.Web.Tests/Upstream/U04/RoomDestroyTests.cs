using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Jobs.Webhooks;
using Campfire.Web.Controllers;
using Campfire.Web.Tests.Controllers.Messages;
using Campfire.Web.Tests.Pipeline;

namespace Campfire.Web.Tests.Upstream.U04;

/// <summary>
/// A room's messages deleted in <c>Room::DestroyJob</c> (14a2f8f), at reference edbc779:
/// reference/test/controllers/rooms_controller_test.rb, models/room_test.rb and models/user_test.rb's
/// cases, on the default parity seed (Designers is closed, HQ open). And the webhook's error reply
/// (d2155e8, models/webhook_test.rb).
/// </summary>
public sealed class RoomDestroyTests : IDisposable
{
    const long designers = 654632876;
    const long hq = 201306877;
    const string davidSession = "AxJs94fteQ5Autv2VrKsH68c";

    readonly MessagesApp messages = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    public void Dispose() => messages.Dispose();

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Destroy_takes_the_room_away_from_its_members_at_once_and_leaves_its_messages_to_a_job()
    {
        var memberIds = await messages.Database.ReadAsync(session => Memberships.UserIdsInRoom(session, designers), Cancellation);
        var messageCount = Count("messages");

        var response = await Delete(designers);

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://{MessagesApp.Host}/", response.Headers.Location.ToString());
        var job = Assert.IsType<RoomDestroyJob>(Assert.Single(messages.Seams.Jobs));
        Assert.Equal(designers, job.RoomId);
        Assert.Equal(memberIds, job.FormerMemberIds);
        Assert.Equal(messageCount, Count("messages"));
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM memberships WHERE room_id = {designers}"));
    }

    [Fact]
    public async Task Destroy_then_the_job_destroys_the_room()
    {
        var rooms = Count("rooms");

        await Delete(designers);
        await messages.PerformEnqueuedJobsAsync();

        Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream == "rooms");
        Assert.Equal(rooms - 1, Count("rooms"));
    }

    [Fact]
    public async Task An_open_room_destroyed_later_lets_no_one_in_who_joins_the_account_before_the_job_runs()
    {
        await Delete(hq);
        var newcomer = await messages.Database.WriteAsync(tx => UserLifecycle.Create(tx, "Newcomer", "newcomer@example.com", null, messages.Now), Cancellation);

        Assert.Equal("Rooms::Closed", messages.Scalar($"SELECT type FROM rooms WHERE id = {hq}"));
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM memberships WHERE room_id = {hq} AND user_id = {newcomer.Id}"));
        await messages.PerformEnqueuedJobsAsync();
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM rooms WHERE id = {hq}"));
    }

    [Fact]
    public async Task A_room_destroyed_later_resets_the_connections_of_its_former_members_as_revoking_their_memberships_would()
    {
        var formerMembers = await messages.Database.ReadAsync(session => Memberships.UserIdsInRoom(session, designers), Cancellation);
        Assert.True(formerMembers.Count > 1);

        await Delete(designers);
        messages.Seams.Clear();
        await new RoomDestroyJobs(messages.Database, messages.Seams.Seams, new FixedClock(messages.Now)).ResetRemoteConnectionsOfAsync(formerMembers, Cancellation);

        Assert.Equal(formerMembers.Order().Select(id => new Disconnect(id, Reconnect: true)), messages.Seams.Disconnects);
    }

    [Fact]
    public async Task Destroying_one_message_at_a_time_leaves_nothing_of_the_room_behind()
    {
        var messageIds = await messages.Database.ReadAsync(session => Messages.InRoom(session, designers).Select(message => message.Id).ToList(), Cancellation);
        var ids = string.Join(',', messageIds);
        var attachments = (long)messages.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'Message' AND record_id IN ({ids})")!;
        Assert.True((long)messages.Scalar($"SELECT COUNT(*) FROM boosts WHERE message_id IN ({ids})")! > 0);
        Assert.True(attachments > 0);
        Assert.True((long)messages.Scalar($"SELECT COUNT(*) FROM message_search_index WHERE rowid IN ({ids})")! > 0);
        var room = await messages.Database.ReadAsync(session => Rooms.Find(session, designers)!, Cancellation);

        await new RoomDestroyJobs(messages.Database, messages.Seams.Seams, new FixedClock(messages.Now)).DestroyOneMessageAtATimeAsync(room, Cancellation);

        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM rooms WHERE id = {designers}"));
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM messages WHERE id IN ({ids})"));
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM boosts WHERE message_id IN ({ids})"));
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM action_text_rich_texts WHERE record_type = 'Message' AND record_id IN ({ids})"));
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'Message' AND record_id IN ({ids})"));
        Assert.Equal(attachments, messages.Seams.Jobs.OfType<PurgeBlobJob>().Count());
        Assert.Equal(0L, messages.Scalar($"SELECT COUNT(*) FROM message_search_index WHERE rowid IN ({ids})"));
    }

    // user_test.rb: one statement grants the open rooms, with the user's own timestamps.
    [Fact]
    public async Task Memberships_granted_to_the_open_rooms_are_like_any_other()
    {
        var user = await messages.Database.WriteAsync(tx => UserLifecycle.Create(tx, "User", "user@example.com", null, messages.Now), Cancellation);

        var openRooms = await messages.Database.ReadAsync(session => Rooms.IdsOfType(session, Campfire.Data.Records.RoomType.Open).Order().ToList(), Cancellation);
        var memberships = await messages.Database.ReadAsync(session => Memberships.ForUser(session, user.Id), Cancellation);
        Assert.Equal(openRooms, memberships.Select(membership => membership.RoomId).Order());
        Assert.All(memberships, membership =>
        {
            Assert.Equal(Campfire.Data.Records.Involvement.Mentions, membership.Involvement);
            Assert.Equal(messages.Now, membership.CreatedAt);
            Assert.Equal(messages.Now, membership.UpdatedAt);
        });
    }

    // webhook_test.rb's "delivery with error reply": an error page is no reply, not an attachment.
    [Theory]
    [InlineData(500, "text/html", "<h1>Internal Error!</h1>")]
    [InlineData(500, "image/png", "png")]
    [InlineData(201, "text/plain", "created")]
    public void A_webhook_reply_other_than_a_200_makes_no_message(int status, string contentType, string body) =>
        Assert.Null(WebhookClient.Reply(status, contentType, System.Text.Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void A_webhook_reply_of_a_200_with_a_file_is_an_attachment() =>
        Assert.Equal("attachment.png", Assert.IsType<WebhookAttachmentReply>(WebhookClient.Reply(200, "image/png", [1, 2, 3])).Filename);

    Task<Response> Delete(long roomId) =>
        messages.SendAsync("DELETE", $"/rooms/{roomId}", messages.SignedIn(davidSession));

    long Count(string table) => (long)messages.Scalar($"SELECT COUNT(*) FROM {table}")!;
}
