using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Lifecycle;

// Messages with their callbacks (reference/app/models/message.rb, message/searchable.rb), in the
// order Rails runs them. Inside the transaction: the rows, then the touches `touch: true`
// defers to just before COMMIT (the rich text touches the message, which touches its room).
// After the commit, in the order the callbacks are declared: the search index, then
// `room.receive` (unread memberships, then the push job).
//
// Every text change and touch of a message re-indexes it after the commit (`after_update_commit
// :update_in_index` fires on a touch too), so each operation that touches a message takes its
// `plainTextBody`.
public static class MessageLifecycle
{
    // `room.messages.create_with_attachment!(body:, client_message_id:)` without an attachment,
    // with Current.user as the creator. `bodyHtml` is the rich text to store; any assigned body,
    // even "", gets a row (`store_if_blank`), and null (no body param) none.
    public static Message Create(
        WriteTransaction transaction,
        DomainSeams seams,
        long roomId,
        long creatorId,
        string? clientMessageId,
        string? bodyHtml,
        string plainTextBody,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        var session = transaction.Session;
        var message = Messages.Create(session, roomId, creatorId, clientMessageId, now);
        if (bodyHtml is not null)
        {
            RichTexts.Create(session, Message.ModelName, message.Id, "body", bodyHtml, now);
            Messages.Touch(session, message.Id, now);
        }
        Rooms.Touch(session, roomId, now);

        transaction.AfterCommit(after =>
        {
            MessageSearchIndex.Create(after, message.Id, plainTextBody);
            Receive(after, seams.Jobs, roomId, message, now);
        });
        return message;
    }

    // messages_controller.rb `deliver_webhooks_to_bots`, which the controller runs once the
    // message is created: in a direct room every active bot member, elsewhere the active bots
    // the body mentions (`mentionees`), never the creator, and only bots with a webhook
    // (`deliver_webhook_later`). Run it after the create's commit, e.g. from AfterCommit after
    // Create.
    public static void DeliverWebhooksToBots(SqliteSession session, IJobQueue jobs, Room room, Message message, IReadOnlyList<long> mentionedUserIds)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(message);
        var bots = room.IsDirect
            ? Users.ActiveBotsInRoom(session, room.Id)
            : Users.InRoomWhereIds(session, room.Id, mentionedUserIds.Distinct().ToList()).Where(user => user.IsBot && user.IsActive).ToList();
        foreach (var bot in bots.Where(bot => bot.Id != message.CreatorId))
        {
            if (Webhooks.ForUser(session, bot.Id) is not null)
            {
                jobs.Enqueue(new WebhookJob(bot.Id, message.Id));
            }
        }
    }

    // `message.update!(body:)`: a changed body updates (or adds) its rich text, which touches the
    // message and its room; the index follows after the commit. An unchanged body writes nothing.
    public static void UpdateBody(WriteTransaction transaction, Message message, string bodyHtml, string plainTextBody, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        var session = transaction.Session;
        if (RichTexts.For(session, Message.ModelName, message.Id, "body") is { } richText)
        {
            if (richText.Body == bodyHtml)
            {
                return;
            }
            RichTexts.UpdateBody(session, richText, bodyHtml, now);
        }
        else
        {
            RichTexts.Create(session, Message.ModelName, message.Id, "body", bodyHtml, now);
        }
        Touch(transaction, message, plainTextBody, now);
    }

    // `message.destroy` of a message without an attachment: each boost (`dependent: :destroy`),
    // the rich text, the message, then the room's touch; the index entry goes after the commit.
    // Touches of the message itself are skipped, as it is being destroyed.
    public static void Destroy(WriteTransaction transaction, Message message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        var session = transaction.Session;
        foreach (var boost in Boosts.ForMessages(session, [message.Id]))
        {
            Boosts.Delete(session, boost.Id);
        }
        if (RichTexts.For(session, Message.ModelName, message.Id, "body") is { } richText)
        {
            RichTexts.Delete(session, richText.Id);
        }
        Messages.Delete(session, message.Id);
        Rooms.Touch(session, message.RoomId, now);

        transaction.AfterCommit(after => MessageSearchIndex.Remove(after, message.Id));
    }

    // A message's `touch` from something that belongs to it (`touch: true` on boosts and rich
    // text): the message, then its room (`belongs_to :room, touch: true`), and the re-index
    // after the commit.
    internal static void Touch(WriteTransaction transaction, Message message, string plainTextBody, DateTimeOffset now)
    {
        Messages.Touch(transaction.Session, message.Id, now);
        Rooms.Touch(transaction.Session, message.RoomId, now);
        transaction.AfterCommit(after => MessageSearchIndex.Update(after, message.Id, plainTextBody));
    }

    // `room.receive(message)` (room.rb): marks the room unread for its visible, disconnected
    // members other than the creator, then enqueues the push.
    static void Receive(SqliteSession session, IJobQueue jobs, long roomId, Message message, DateTimeOffset now)
    {
        Memberships.MarkUnread(session, roomId, message.CreatorId, message.CreatedAt, now);
        jobs.Enqueue(new PushMessageJob(roomId, message.Id));
    }
}
