using Campfire.Data.MessageAttachments;
using Campfire.Data.Records;
using Campfire.Jobs.Webhooks;
using Campfire.RailsCompat.Params;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Web.Pipeline;

namespace Campfire.Web.Controllers;

// The bot API's attachment half (reference/app/controllers/messages/by_bots_controller.rb):
// `create_with_attachment!` of a top-level multipart `attachment` (no body, no client message id)
// and `destroy` of a bot message that has a file. MessagesByBotsController left these as hooks.
public sealed partial class MessagesByBotsController
{
    // `params.permit(:attachment)`: an upload, or a blob's signed id (`Blob.find_signed!`).
    partial void CreateWithAttachment(ParamHash attributes, ref Func<ValueTask<Message>>? create)
    {
        create = attributes["attachment"] switch
        {
            UploadedFile file => async () => await CreateWithUploadAsync(file).ConfigureAwait(false),
            string signedId => async () => await CreateWithSignedIdAsync(signedId).ConfigureAwait(false),
            var other => throw new InvalidOperationException($"Could not find or build blob: expected attachable, got {other}"),
        };
    }

    // `@message.destroy` of a message with a file: the attachment row goes in the message's
    // transaction and, after the commit, the blob is purged later.
    partial void DestroyWithAttachment(Message message, ref Func<ValueTask>? destroy)
    {
        destroy = async () =>
        {
            var seams = App.RequireSeams();
            await WriteAsync(transaction =>
            {
                MessageAttachmentLifecycle.DestroyWithAttachment(transaction, seams, message, Now);
                return true;
            }).ConfigureAwait(false);
        };
    }

    // `create_with_attachment!` with a file. The creator is the bot; there is no body and no
    // client message id, so the message gets a UUID and indexes the filename.
    async ValueTask<Message> CreateWithUploadAsync(UploadedFile file)
    {
        var storage = App.RequireStorage();
        using var staged = storage.Stage(file);
        var (room, user, now, seams) = (CurrentRoom, User, Now, App.RequireSeams());
        var filename = staged.Blob.Filename.ToString();
        var message = await WriteAsync(transaction =>
        {
            transaction.AfterCommit(_ => staged.Keep());
            return MessageAttachmentLifecycle.CreateWithAttachment(
                transaction, seams, room.Id, user.Id, null, null,
                RichTextPlainText.PlainTextBody(null, filename, RichTextContext(transaction.Session)),
                new StagedUpload(staged.Blob.Key, staged.Blob.Filename.Value, staged.Blob.ContentType, staged.Blob.Metadata,
                    staged.Blob.ServiceName, staged.Blob.ByteSize, staged.Blob.Checksum),
                now);
        }).ConfigureAwait(false);
        await MessageAttachmentProcessing.ProcessAsync(
            App.Database, App.Keys, storage, seams, App.Clock, RequestUrl.Host, now, message, RequestAborted).ConfigureAwait(false);
        return message;
    }

    async ValueTask<Message> CreateWithSignedIdAsync(string signedId)
    {
        var storage = App.RequireStorage();
        var id = storage.Urls.VerifySignedId(signedId, Now)
            ?? throw new InvalidOperationException("ActiveSupport::MessageVerifier::InvalidSignature");
        var blob = await ReadAsync(session => BlobRecords.FindBlob(session, id)).ConfigureAwait(false)
            ?? throw new RecordNotFoundException($"Couldn't find ActiveStorage::Blob with 'id'={id}");
        var (room, user, now, seams) = (CurrentRoom, User, Now, App.RequireSeams());
        var message = await WriteAsync(transaction => MessageAttachmentLifecycle.CreateWithExistingBlob(
            transaction, seams, room.Id, user.Id, null, null,
            RichTextPlainText.PlainTextBody(null, blob.Filename.ToString(), RichTextContext(transaction.Session)),
            blob.Id, blob.IsAnalyzed, now)).ConfigureAwait(false);
        await MessageAttachmentProcessing.ProcessAsync(
            App.Database, App.Keys, storage, seams, App.Clock, RequestUrl.Host, now, message, RequestAborted).ConfigureAwait(false);
        return message;
    }
}

// A webhook's attachment reply (reference/app/models/webhook.rb `extract_attachment_from` and
// `receive_attachment_reply_to`): `Blob.create_and_upload!` of the response, then
// `create_with_attachment!(attachment:, creator:)` and `process_attachment`. The caller broadcasts.
public sealed partial class ByBotsWebhookReplies
{
    partial void CreateAttachmentReply(Room room, User bot, WebhookAttachmentReply reply, ref Func<CancellationToken, Task<Message>>? create)
    {
        create = cancellationToken => CreateAttachmentReplyAsync(room, bot, reply, cancellationToken);
    }

    // `ActiveStorage::Blob.create_and_upload!(io:, filename:, content_type:)` identifies the bytes
    // with Marcel even though a type was declared, in its own transaction, and then the message
    // attaches that blob. There is no request, so plain text and the analyze touch have no host.
    async Task<Message> CreateAttachmentReplyAsync(Room room, User bot, WebhookAttachmentReply reply, CancellationToken cancellationToken)
    {
        var storage = app.RequireStorage();
        var seams = app.RequireSeams();
        var now = app.Clock.GetUtcNow();
        Blob blob;
        using (var source = new MemoryStream(reply.Data, writable: false))
        using (var staged = storage.Stage(source, new Filename(reply.Filename), reply.ContentType))
        {
            blob = await app.Database.WriteAsync(transaction => BlobStorage.Create(transaction, staged, now), cancellationToken).ConfigureAwait(false);
        }
        var message = await app.Database.WriteAsync(transaction => MessageAttachmentLifecycle.CreateWithExistingBlob(
            transaction, seams, room.Id, bot.Id, null, null,
            RichTextPlainText.PlainTextBody(null, blob.Filename.ToString(), RichTextContext(transaction.Session, now)),
            blob.Id, blob.IsAnalyzed, now), cancellationToken).ConfigureAwait(false);
        await MessageAttachmentProcessing.ProcessAsync(
            app.Database, app.Keys, storage, seams, app.Clock, null, now, message, cancellationToken).ConfigureAwait(false);
        return message;
    }
}
