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
                PlainTextBody(body, attachment),
                attachment,
                [.. boosts[message.Id].Select(boost => new BoostView(
                    boost.Id, boost.Content, boost.CreatedAt, users.GetValueOrDefault(boost.BoosterId), boost.MessageId, message.RoomId))]);
        })];
    }

    /// <summary>The user's fields as the partials read them.</summary>
    public MessageUser User(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return MessageUser.From(user, TransferableUser.GenerateAvatarSignedId(keys, user.Id));
    }

    // `body.to_plain_text.presence || attachment&.filename&.to_s || ""`
    string PlainTextBody(string? body, Blob? attachment)
    {
        var plainText = body is null ? "" : toPlainText(body);
        return RubyValues.IsPresent(plainText) ? plainText : attachment?.Filename.ToString() ?? "";
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
