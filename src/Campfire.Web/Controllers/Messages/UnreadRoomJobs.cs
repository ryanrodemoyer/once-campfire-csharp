using Campfire.Cable.Channels;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Message::BroadcastUnreadRoomJob</c> (reference/app/jobs/message/broadcast_unread_room_job.rb):
/// <c>message.broadcast_unread_room</c>, the fan-out <c>broadcast_create</c> leaves to a job because
/// in a big room it takes far longer than the rest of posting a message
/// (<see cref="UnreadRoomsChannel.BroadcastUnread"/>).
/// </summary>
public sealed class UnreadRoomJobs(SqliteDatabase database, IBroadcaster broadcaster, TimeProvider clock)
{
    public void RegisterWith(JobRunner runner, JobQueueLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Register<BroadcastUnreadRoomJob>(PerformAsync, limits);
    }

    /// <summary>
    /// <c>perform(message)</c>. A message that's gone raises, as Active Job's
    /// <c>DeserializationError</c> does: ApplicationJob doesn't discard it.
    /// </summary>
    public Task PerformAsync(BroadcastUnreadRoomJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return database.ReadAsync(session =>
        {
            var message = Messages.Find(session, job.MessageId)
                ?? throw new InvalidOperationException($"ActiveJob::DeserializationError: Couldn't find Message with 'id'={job.MessageId}");
            UnreadRoomsChannel.BroadcastUnread(broadcaster, session, message, clock.GetUtcNow());
            return true;
        }, cancellationToken);
    }
}
