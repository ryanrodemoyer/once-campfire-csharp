using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.RichText.Sanitize;
using Campfire.Storage.Blobs;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>Room::PushMessageJob</c> (reference/app/jobs/room/push_message_job.rb), which runs
/// <c>Room::MessagePusher#push</c> (reference/app/models/room/message_pusher.rb): the message goes
/// to the push subscriptions of the room's active, visible, disconnected members other than its
/// creator, those involved in everything and the mentioned ones involved in mentions together.
/// </summary>
/// <param name="database">The app's database.</param>
/// <param name="pool">The pool the notifications are queued on.</param>
/// <param name="attachables">
/// Action Text's record lookups for a session (the web app's <c>DatabaseAttachables</c>), which
/// <c>plain_text_body</c> and <c>mentionees</c> resolve the body's attachments with.
/// </param>
/// <param name="clock">When "disconnected" is measured from.</param>
public sealed class MessagePusher(SqliteDatabase database, WebPushPool pool, Func<SqliteSession, IAttachableResolver> attachables, TimeProvider clock)
{
    public void RegisterWith(JobRunner runner, JobQueueLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Register<PushMessageJob>(PerformAsync, limits);
    }

    /// <summary>
    /// <c>perform(room, message)</c>. A room or message that's gone raises, as Active Job's
    /// <c>DeserializationError</c> does.
    /// </summary>
    public Task PerformAsync(PushMessageJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return database.ReadAsync(session =>
        {
            var room = Rooms.Find(session, job.RoomId) ?? throw Missing("Room", job.RoomId);
            var message = Messages.Find(session, job.MessageId) ?? throw Missing("Message", job.MessageId);
            return Push(session, room, message);
        }, cancellationToken);
    }

    /// <summary>
    /// <c>push</c>: queues the notifications and returns the payload, or null when nobody is to
    /// be notified: then no payload is built.
    /// </summary>
    public PushPayload? Push(SqliteSession session, Room room, Message message)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(message);
        var context = new RenderContext(attachables(session), RequestHost: null);
        var body = RichTexts.For(session, Message.ModelName, message.Id, "body")?.Body;
        var subscriptions = PushSubscriptions.ForRoomPush(session, room.Id, message.CreatorId, clock.GetUtcNow(), MentionedUserIds(body, context));
        if (subscriptions.Count == 0)
        {
            return null;
        }
        var payload = BuildPayload(room, Creator(session, message), PlainTextBody(session, message, body, context));
        pool.Queue(session, payload, subscriptions);
        return payload;
    }

    /// <summary>
    /// <c>build_payload</c>: a direct message is titled with its creator's name, anything else
    /// with the room's name and its body prefixed with the creator's.
    /// </summary>
    public static PushPayload BuildPayload(Room room, User creator, string plainTextBody)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(creator);
        var path = $"/rooms/{room.Id}";
        return room.IsDirect
            ? new PushPayload(creator.Name, plainTextBody, path)
            : new PushPayload(room.Name, $"{creator.Name}: {plainTextBody}", path);
    }

    /// <summary>
    /// <c>mentioned_users</c> (message/mentionee.rb): <c>body.body.attachables.grep(User).uniq</c>,
    /// as ids. <c>mentionees</c> narrows them to the room's users, which the subscription query
    /// does by joining the room's memberships.
    /// </summary>
    public static List<long> MentionedUserIds(string? body, RenderContext context)
    {
        if (body is null)
        {
            return [];
        }
        // Content.new canonicalizes (minify, galleries, unwrap raw markdown). Lexxy's
        // as_canonical leaves figure[data-trix-attachment] in place, so those are not attachments.
        var fragment = RichTextRenderer.Wrap(body);
        ContentFilters.CanonicalizeContent(fragment);
        return [.. AttachmentResolution.AttachmentNodes(fragment)
            .Select(node => AttachmentResolution.ActionTextAttachableFromNode(node, context))
            .OfType<Mention>()
            .Select(mention => mention.User.Id)
            .Distinct()];
    }

    // `message.plain_text_body`
    static string PlainTextBody(SqliteSession session, Message message, string? body, RenderContext context) =>
        RichTextPlainText.PlainTextBody(body, BlobRecords.FindAttachedBlob(session, Message.ModelName, message.Id, "attachment")?.Filename.ToString(), context);

    // `message.creator`: `belongs_to`, so a missing user raises NoMethodError on `.name`.
    static User Creator(SqliteSession session, Message message) =>
        Users.Find(session, message.CreatorId) ?? throw new InvalidOperationException($"undefined method 'name' for nil (Message {message.Id} has no creator)");

    static InvalidOperationException Missing(string model, long id) =>
        new($"ActiveJob::DeserializationError: Couldn't find {model} with 'id'={id}");
}
