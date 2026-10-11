using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;

namespace Campfire.Web.Controllers;

/// <summary>
/// A room's destruction, in two halves (reference/app/models/room.rb): <c>destroy_later</c>, which
/// rooms#destroy runs, takes the room away from its members at once; <c>Room::DestroyJob</c>
/// (reference/app/jobs/room/destroy_job.rb) then destroys its messages one at a time and the room.
/// Destroying them all in the room's own transaction held the write lock, and stopped every other
/// write, until the last one.
/// </summary>
public sealed class RoomDestroyJobs(SqliteDatabase database, DomainSeams seams, TimeProvider clock)
{
    public void RegisterWith(JobRunner runner, JobQueueLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Register<RoomDestroyJob>(PerformAsync, limits);
    }

    /// <summary>
    /// <c>destroy_later</c>: an open room is closed first, so that no one who joins the account
    /// before the job runs is let in; then, in one transaction, the room is saved and its
    /// memberships deleted (without callbacks). The job gets the former members' ids.
    /// </summary>
    public static async Task DestroyLaterAsync(SqliteDatabase database, IJobQueue jobs, Room room, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(room);
        var formerMemberIds = await database.WriteAsync(transaction =>
        {
            var session = transaction.Session;
            if (room.IsOpen)
            {
                // `becomes!(Rooms::Closed)`, then `save!`
                Rooms.Update(session, room, now, type: RoomType.Closed);
            }
            var userIds = Memberships.UserIdsInRoom(session, room.Id);
            Memberships.DeleteForRoom(session, room.Id);
            return userIds;
        }).ConfigureAwait(false);

        jobs.Enqueue(new RoomDestroyJob(room.Id, formerMemberIds));
    }

    /// <summary>
    /// <c>perform(room, former_member_ids)</c>: the former members' connections are reset, as
    /// revoking their memberships would have, then the messages go one at a time and the room
    /// after them. A room that's gone is discarded (<c>discard_on ActiveJob::DeserializationError</c>).
    /// </summary>
    public async Task PerformAsync(RoomDestroyJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var room = await database.ReadAsync(session => Rooms.Find(session, job.RoomId), cancellationToken).ConfigureAwait(false);
        if (room is null)
        {
            return;
        }
        await ResetRemoteConnectionsOfAsync(job.FormerMemberIds, cancellationToken).ConfigureAwait(false);
        await DestroyOneMessageAtATimeAsync(room, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>reset_remote_connections_of(user_ids)</c>: <c>User.where(id:).find_each(&amp;:reset_remote_connections)</c>,
    /// so the former members' connections stop receiving the room's streams.
    /// </summary>
    public async Task ResetRemoteConnectionsOfAsync(IReadOnlyList<long> userIds, CancellationToken cancellationToken = default)
    {
        var users = await database.ReadAsync(session => Users.WhereIds(session, userIds), cancellationToken).ConfigureAwait(false);
        foreach (var user in users.OrderBy(user => user.Id))
        {
            seams.Connections.Disconnect(user.Id, reconnect: true);
        }
    }

    /// <summary>
    /// <c>destroy_one_message_at_a_time</c>: <c>messages.find_each(&amp;:destroy)</c>, each message in
    /// its own transaction so other writes get through in between, then <c>destroy</c>, which takes
    /// any message posted meanwhile with the room.
    /// </summary>
    public async Task DestroyOneMessageAtATimeAsync(Room room, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        var messages = await database.ReadAsync(session => Messages.InRoom(session, room.Id), cancellationToken).ConfigureAwait(false);
        foreach (var message in messages.OrderBy(message => message.Id))
        {
            await database.WriteAsync(transaction =>
            {
                if (Messages.Find(transaction.Session, message.Id) is { } current)
                {
                    MessageAttachmentLifecycle.DestroyWithAttachment(transaction, seams, current, clock.GetUtcNow());
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        await database.WriteAsync(transaction =>
        {
            Destroy(transaction, seams, room.Id, clock.GetUtcNow());
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>room.destroy</c>: its memberships deleted without callbacks (<c>dependent: :delete_all</c>),
    /// each message destroyed with its callbacks (<c>dependent: :destroy</c>), then the room.
    /// </summary>
    public static void Destroy(WriteTransaction transaction, DomainSeams seams, long roomId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var session = transaction.Session;
        Memberships.DeleteForRoom(session, roomId);
        foreach (var message in Messages.InRoom(session, roomId))
        {
            MessageAttachmentLifecycle.DestroyWithAttachment(transaction, seams, message, now);
        }
        Rooms.Delete(session, roomId);
    }
}
