using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;

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
        // `with_boosts` preloads every message's boosts at once, and messages/boosts/_boosts sorts the
        // loaded ones by `created_at` (b6d3075); the preload's id order breaks ties.
        var preloaded = Boosts.ForMessages(session, ids).ToLookup(boost => boost.MessageId);
        var boosts = messages.ToDictionary(message => message.Id, message => preloaded[message.Id].OrderBy(boost => boost.CreatedAt).ToList());
        var userIds = messages.Select(message => message.CreatorId).Concat(boosts.Values.SelectMany(list => list.Select(boost => boost.BoosterId))).Distinct().ToList();
        var users = Users.WhereIds(session, userIds).ToDictionary(user => user.Id, User);
        var rooms = new Dictionary<long, MessageRoom?>();

        var attachments = BlobRecords.FindAttachedBlobs(session, Message.ModelName, ids, "attachment");
        var imageBlobs = attachments.Values.Where(b => b.IsVariable).ToList();
        var processedThumbBlobIds = new HashSet<long>();
        if (imageBlobs.Count > 0)
        {
            var digests = imageBlobs.ToDictionary(b => b.Id, b => Representable.Variant(b, NamedVariants.MessageThumb).Variation.Digest);
            var records = Campfire.Storage.Variants.VariantRecords.ForBlobs(session, [.. digests.Keys]);
            foreach (var rec in records)
            {
                if (digests.TryGetValue(rec.BlobId, out var expectedDigest) && rec.VariationDigest == expectedDigest)
                {
                    processedThumbBlobIds.Add(rec.BlobId);
                }
            }
        }

        var videoBlobIds = attachments.Values.Where(b => b.IsVideo).Select(b => b.Id).ToList();
        var videoPreviewBlobIds = BlobRecords.FindAttachmentBlobIds(session, VariantProcessor.PreviewImageRecordType, videoBlobIds, VariantProcessor.PreviewImageName);
        var processedPosterPreviewIds = new HashSet<long>();
        if (videoPreviewBlobIds.Count > 0)
        {
            var posterDigest = new Variation(NamedVariants.VideoPoster).Digest;
            var records = Campfire.Storage.Variants.VariantRecords.ForBlobs(session, [.. videoPreviewBlobIds.Values]);
            foreach (var rec in records)
            {
                if (rec.VariationDigest == posterDigest)
                {
                    processedPosterPreviewIds.Add(rec.BlobId);
                }
            }
        }

        return [.. messages.Select(message =>
        {
            var body = bodies.GetValueOrDefault(message.Id);
            var attachment = attachments.GetValueOrDefault(message.Id);
            var isThumbProcessed = attachment is not null && processedThumbBlobIds.Contains(attachment.Id);
            var isPosterProcessed = attachment is not null && videoPreviewBlobIds.TryGetValue(attachment.Id, out var previewBlobId) && processedPosterPreviewIds.Contains(previewBlobId);
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
                plain.Failed,
                isThumbProcessed,
                isPosterProcessed);
        })];
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
        return new MessageRoom(room.Id, View.RoomDisplayName(room.Name, room.IsDirect, members), room.IsDirect, room.Type.ClassName());
    }
}
