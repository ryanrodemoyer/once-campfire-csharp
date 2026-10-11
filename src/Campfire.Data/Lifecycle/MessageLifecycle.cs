using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Lifecycle;

// Messages with their callbacks (reference/app/models/message.rb, message/searchable.rb), in the
// order Rails runs them. Creating a message is atomic: the message, its rich text and attachment
// (autosaved), its search entry and its room's unread memberships (`after_save
// :record_creation`), then the touches `touch: true` defers to just before COMMIT (the rich text
// touches the message, which touches its room), all in one transaction. Only the push job waits
// for the commit (`after_create_commit -> { room.push_later(self) }`).
//
// After that, the search entry is rewritten only when the body is saved or the attachment
// replaced (`after_update_commit :update_in_index, if: :indexed_text_changed?`): a boost's or an
// analysis's touch leaves it alone.
public static class MessageLifecycle
{
    // `room.messages.create_with_attachment!(body:, client_message_id:)` without an attachment,
    // with Current.user as the creator. `bodyHtml` is the rich text to store; any assigned body,
    // even "", gets a row (`store_if_blank`), and null (no body param) none. `attach` saves the
    // message's attachment rows, as Active Storage's autosave does, before the search entry
    // takes the plain text (which is the attachment's filename when there is no text).
    public static Message Create(
        WriteTransaction transaction,
        DomainSeams seams,
        long roomId,
        long creatorId,
        string? clientMessageId,
        string? bodyHtml,
        string plainTextBody,
        DateTimeOffset now,
        Action<SqliteSession, Message>? attach = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        var session = transaction.Session;
        var message = Messages.Create(session, roomId, creatorId, clientMessageId, now);
        attach?.Invoke(session, message);
        if (bodyHtml is not null)
        {
            RichTexts.Create(session, Message.ModelName, message.Id, "body", bodyHtml, now);
        }
        RecordCreation(session, roomId, message, plainTextBody, now);
        if (bodyHtml is not null || attach is not null)
        {
            Messages.Touch(session, message.Id, now);
        }
        Rooms.Touch(session, roomId, now);

        transaction.AfterCommit(_ => PushLater(seams.Jobs, roomId, message));
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
        Touch(transaction, message, now);
        UpdateInIndexAfterCommit(transaction, message, plainTextBody);
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

    // A message's `touch` from something that belongs to it (`touch: true` on boosts, rich text
    // and attachments): the message, then its room (`belongs_to :room, touch: true`). The search
    // entry stays as it is (`indexed_text_changed?`).
    internal static void Touch(WriteTransaction transaction, Message message, DateTimeOffset now)
    {
        Messages.Touch(transaction.Session, message.Id, now);
        Rooms.Touch(transaction.Session, message.RoomId, now);
    }

    // `update_in_index` after the commit of a save that changed the body or replaced the
    // attachment.
    public static void UpdateInIndexAfterCommit(WriteTransaction transaction, Message message, string plainTextBody)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        transaction.AfterCommit(after => MessageSearchIndex.Update(after, message.Id, plainTextBody));
    }

    // `room.receive(message)` (room.rb), which nothing in the app calls any more but stays
    // public: the unread memberships, then the push job.
    public static void Receive(SqliteSession session, IJobQueue jobs, Room room, Message message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(message);
        Memberships.MarkUnread(session, room.Id, room.IsDirect, message.CreatorId, message.CreatedAt, now);
        PushLater(jobs, room.Id, message);
    }

    // `record_creation`: the search entry, then `room.unread_memberships(self)`.
    static void RecordCreation(SqliteSession session, long roomId, Message message, string plainTextBody, DateTimeOffset now)
    {
        MessageSearchIndex.Create(session, message.Id, plainTextBody);
        var room = Rooms.Find(session, roomId) ?? throw new InvalidOperationException($"Message {message.Id} has no room");
        Memberships.MarkUnread(session, roomId, room.IsDirect, message.CreatorId, message.CreatedAt, now);
    }

    // `room.push_later(message)`
    static void PushLater(IJobQueue jobs, long roomId, Message message) =>
        jobs.Enqueue(new PushMessageJob(roomId, message.Id));
}
