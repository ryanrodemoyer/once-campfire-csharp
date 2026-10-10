using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;

namespace Campfire.Web.Helpers;

/// <summary>
/// Loads <see cref="MessageView"/>s as <c>Message.with_presentation</c> loads messages for
/// <c>messages/_message</c>: creators, rooms, rich text bodies, attachments and ordered boosts.
/// </summary>
/// <param name="keys">The app's key generator, which signs avatar tokens.</param>
/// <param name="toPlainText">
/// <c>ActionText::Content#to_plain_text</c> of a stored body, for <c>plain_text_body</c>.
/// </param>
public sealed class MessageViews(KeyGenerator keys, Func<string, string> toPlainText)
{
    /// <summary>The messages, in the given order.</summary>
    public List<MessageView> Load(SqliteSession session, IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var ids = messages.Select(message => message.Id).ToList();
        var bodies = RichTexts.ForRecords(session, Message.ModelName, "body", ids).ToDictionary(richText => richText.RecordId, richText => richText.Body);
        var boosts = messages.ToDictionary(message => message.Id, message => Boosts.ForMessageOrdered(session, message.Id));
        var userIds = messages.Select(message => message.CreatorId).Concat(boosts.Values.SelectMany(list => list.Select(boost => boost.BoosterId))).Distinct().ToList();
        var users = Users.WhereIds(session, userIds).ToDictionary(user => user.Id, User);
        var rooms = new Dictionary<long, MessageRoom?>();

        return [.. messages.Select(message =>
        {
            var body = bodies.GetValueOrDefault(message.Id);
            var attachment = BlobRecords.FindAttachedBlob(session, Message.ModelName, message.Id, "attachment");
            var plain = PlainTextBody(body, attachment);
            var room = rooms.TryGetValue(message.RoomId, out var loaded) ? loaded : rooms[message.RoomId] = LoadRoom(session, message.RoomId);
            return new MessageView(
                message.Id,
                message.ClientMessageId,
                message.CreatedAt,
                message.UpdatedAt,
                message.CreatorId,
                room ?? throw new InvalidOperationException($"Message {message.Id}'s room {message.RoomId} is gone"),
                users.GetValueOrDefault(message.CreatorId),
                body,
                plain.Text,
                attachment,
                [.. boosts[message.Id].Select(boost => new BoostView(
                    boost.Id, boost.Content, boost.CreatedAt, users.GetValueOrDefault(boost.BoosterId), boost.MessageId, message.RoomId))],
                plain.Failed);
        })];
    }

    /// <summary>
    /// <see cref="Load"/> for one message whose rich text body is <paramref name="body"/> rather
    /// than the stored row. The differential fuzzer holds that body only in memory: the oracle
    /// updates it in place and does not export <c>action_text_rich_texts</c>.
    /// </summary>
    public MessageView WithBody(SqliteSession session, Message message, string? body)
    {
        var loaded = Load(session, [message])[0];
        var plain = PlainTextBody(body, loaded.Attachment);
        return loaded with { Body = body, PlainTextBody = plain.Text, PlainTextFailed = plain.Failed };
    }

    /// <summary>The user's fields as the partials read them.</summary>
    public MessageUser User(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return MessageUser.From(user, TransferableUser.GenerateAvatarSignedId(keys, user.Id));
    }

    // `body.to_plain_text.presence || attachment&.filename&.to_s || ""`.
    // message_tag calls this for the emoji class and rescues Exception, so one bad body blanks
    // that message instead of failing the room page (reference/app/helpers/messages_helper.rb).
    (string Text, bool Failed) PlainTextBody(string? body, Blob? attachment)
    {
        try
        {
            var plainText = body is null ? "" : toPlainText(body);
            return (RubyValues.IsPresent(plainText) ? plainText : attachment?.Filename.ToString() ?? "", false);
        }
#pragma warning disable CA1031 // Rails rescues Exception around the whole message tag.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ("", true);
        }
    }

    static MessageRoom? LoadRoom(SqliteSession session, long roomId)
    {
        if (Rooms.Find(session, roomId) is not { } room)
        {
            return null;
        }
        var members = room.IsDirect ? Users.InRoom(session, room.Id).Select(user => (user.Id, user.Name)) : [];
        return new MessageRoom(room.Id, View.RoomDisplayName(room.Name, room.IsDirect, members));
    }
}
