using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Ruby;
using Gid = Campfire.RailsCompat.GlobalId.GlobalId;

namespace Campfire.Jobs;

// `RemoveBannedContentJob` (reference/app/jobs/remove_banned_content_job.rb): runs
// `user.remove_banned_content` (reference/app/models/user/bannable.rb), which loads the user's
// messages and, one at a time, destroys each in its own transaction, then broadcasts its removal
// (`broadcast_remove`, reference/app/models/message/broadcasts.rb).
public sealed class RemoveBannedContent(SqliteDatabase database, IBroadcaster broadcaster, IJobQueue jobQueue, TimeProvider clock)
{
    public RemoveBannedContent(SqliteDatabase database, IBroadcaster broadcaster, TimeProvider clock)
        : this(database, broadcaster, broadcaster as IJobQueue ?? new DiscardingJobQueue(), clock)
    {
    }

    sealed class DiscardingJobQueue : IJobQueue
    {
        public void Enqueue(Job job) { }
    }

    public void RegisterWith(JobRunner runner, JobQueueLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Register<RemoveBannedContentJob>(PerformAsync, limits);
    }

    public async Task PerformAsync(RemoveBannedContentJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var messages = await database.ReadAsync(session => Messages.ByCreator(session, job.UserId), cancellationToken).ConfigureAwait(false);
        foreach (var message in messages)
        {
            var room = await database.WriteAsync(transaction =>
            {
                MessageAttachmentLifecycle.DestroyWithAttachment(transaction, jobQueue, message, clock.GetUtcNow());
                return Rooms.Find(transaction.Session, message.RoomId);
            }, cancellationToken).ConfigureAwait(false);
            if (room is not null)
            {
                broadcaster.Broadcast(MessagesStream(room), RemovePayload(message));
            }
        }
    }

    // `[room, :messages]` as Turbo::StreamsChannel names it: the room's `to_gid_param`, which
    // carries its STI class name, then "messages".
    internal static string MessagesStream(Room room) =>
        $"{Gid.Create(room.Type.ClassName(), room.Id).ToParam()}:messages";

    // `broadcast_remove_to room, :messages` with the message as the target: a template-less
    // remove action for `dom_id(message)`, which is keyed by client_message_id (`to_key`),
    // JSON-encoded as ActionCable.server.broadcast's default coder does.
    internal static string RemovePayload(Message message)
    {
        var target = RubyEscape.HtmlEscape($"message_{message.ClientMessageId}");
        return RailsJson.Encode(JsonValue.Create($"<turbo-stream action=\"remove\" target=\"{target}\"></turbo-stream>"));
    }
}
