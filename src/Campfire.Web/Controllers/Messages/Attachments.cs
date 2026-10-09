using Campfire.Data.MessageAttachments;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using DataQueries = Campfire.Data.Queries;

namespace Campfire.Web.Controllers;

// The attachment half of MessagesWriteController, which M05 left as hooks (message/attachment.rb,
// the Active Storage gem behind it, and app/javascript/models/file_uploader.js driving them): the
// file uploader's multipart XHR create — `create_with_attachment!` with a file, which puts the
// blob and attachment rows in the message's transaction, uploads the file once it commits, and
// then runs `process_attachment` (the analysis, and the thumbnail or the video preview) before the
// broadcasts — and `destroy` of a message with a file, whose blob is purged later. Without these,
// either answers 501; the jobs the flow enqueues are performed by MessageAttachmentJobs
// (AttachmentsJobs.cs).
public sealed partial class MessagesWriteController
{
    // `@room.messages.create_with_attachment!(message_params)` (messages_controller.rb create) with
    // a file in params[:message][:attachment]: an upload (an UploadedFile), or any other string a
    // blob's signed id (`Blob.find_signed!`).
    partial void CreateWithAttachment(ParamHash attributes, string? storedBody, ref Func<ValueTask<Message>>? create)
    {
        create = attributes["attachment"] switch
        {
            UploadedFile file => async () => await CreateWithUploadAsync(file, attributes, storedBody).ConfigureAwait(false),
            string signedId => async () => await CreateWithSignedIdAsync(signedId, attributes, storedBody).ConfigureAwait(false),
            var other => throw new InvalidOperationException($"Could not find or build blob: expected attachable, got {other}"),
        };
    }

    // `@message.destroy` of a message with a file: the attachment row goes in the message's
    // transaction and, after the commit, the blob is purged later (purge_later).
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

    // `create_with_attachment!` with a file: `Blob.build_after_unfurling` plus the upload — the
    // file staged into the service before the rows (its checksum, its size and its content type,
    // identified from the bytes), kept once the rows commit — then the rows, then
    // `process_attachment` once they've committed.
    async ValueTask<Message> CreateWithUploadAsync(UploadedFile file, ParamHash attributes, string? storedBody)
    {
        var storage = App.RequireStorage();
        using var staged = storage.Stage(file);
        var (room, user, now, seams) = (CurrentRoom, User, Now, App.RequireSeams());
        var filename = staged.Blob.Filename.ToString();
        var message = await WriteAsync(transaction =>
        {
            // `CreateOne#upload` after the commit: keep the staged file.
            transaction.AfterCommit(_ => staged.Keep());
            return MessageAttachmentLifecycle.CreateWithAttachment(
                transaction, seams, room.Id, user.Id, ClientMessageId(attributes), storedBody,
                RichTextPlainText.PlainTextBody(storedBody, filename, RichTextContext(transaction.Session)),
                new StagedUpload(staged.Blob.Key, staged.Blob.Filename.Value, staged.Blob.ContentType, staged.Blob.Metadata,
                    staged.Blob.ServiceName, staged.Blob.ByteSize, staged.Blob.Checksum),
                now);
        }).ConfigureAwait(false);
        await ProcessAttachmentAsync(message).ConfigureAwait(false);
        return message;
    }

    // `create_with_attachment!` with a blob's signed id (`Blob.find_signed!`): no file to upload and
    // no rows to keep; `process_attachment` finds everything already made, so it writes nothing.
    async ValueTask<Message> CreateWithSignedIdAsync(string signedId, ParamHash attributes, string? storedBody)
    {
        var storage = App.RequireStorage();
        // A bad signature raises `ActiveSupport::MessageVerifier::InvalidSignature` (a 500; the
        // rescue_responses don't map it), a good one for a missing blob `ActiveRecord::RecordNotFound`.
        var id = storage.Urls.VerifySignedId(signedId, Now)
            ?? throw new InvalidOperationException("ActiveSupport::MessageVerifier::InvalidSignature");
        var blob = await ReadAsync(session => BlobRecords.FindBlob(session, id)).ConfigureAwait(false)
            ?? throw new RecordNotFoundException($"Couldn't find ActiveStorage::Blob with 'id'={id}");
        var (room, user, now, seams) = (CurrentRoom, User, Now, App.RequireSeams());
        var message = await WriteAsync(transaction => MessageAttachmentLifecycle.CreateWithExistingBlob(
            transaction, seams, room.Id, user.Id, ClientMessageId(attributes), storedBody,
            RichTextPlainText.PlainTextBody(storedBody, blob.Filename.ToString(), RichTextContext(transaction.Session)),
            blob.Id, blob.IsAnalyzed, now)).ConfigureAwait(false);
        await ProcessAttachmentAsync(message).ConfigureAwait(false);
        return message;
    }

    // `process_attachment` (message/attachment.rb), once `create_with_attachment!` has committed.
    // The bot API and a webhook's attachment reply run the same processing.
    async ValueTask ProcessAttachmentAsync(Message message) =>
        await MessageAttachmentProcessing.ProcessAsync(
            App.Database, App.Keys, App.RequireStorage(), App.RequireSeams(), App.Clock, RequestUrl.Host, Now, message, RequestAborted).ConfigureAwait(false);

    // `blob.analyze` — `ensure_attachment_analyzed`'s half of process_attachment, and
    // ActiveStorage::AnalyzeJob#perform's: the analyzer's metadata read outside the writer, then
    // `update!(metadata:)` in one write. `update!` writes nothing when the metadata is unchanged, as
    // it is for a blob analyzed once (so a repeat analyze touches nothing), and its `after_update
    // :touch_attachments` touches each message attachment's message — then its room, re-indexed
    // after the commit.
    internal static async Task AnalyzeAsync(SqliteDatabase database, KeyGenerator keys, BlobStorage storage, string? host, Blob blob, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(blob);
        var metadata = new MediaProcessor(storage).AnalyzedMetadata(blob);
        if (RailsJson.Encode(blob.Metadata) == RailsJson.Encode(metadata))
        {
            return;
        }
        await database.WriteAsync(transaction =>
        {
            var session = transaction.Session;
            BlobRecords.UpdateMetadata(session, blob.Id, metadata);
            var context = new RenderContext(new DatabaseAttachables(session, keys, now), host);
            foreach (var message in MessagesAttachedTo(session, blob))
            {
                MessageAttachmentLifecycle.Touch(transaction, message,
                    RichTextPlainText.PlainTextBody(DataQueries.RichTexts.For(session, Message.ModelName, message.Id, AttachmentNames.Body)?.Body, blob.Filename.ToString(), context),
                    now);
            }
            return true;
        }).ConfigureAwait(false);
    }

    // `blob.attachments.includes(:record)` for the messages of the blob's message attachments
    // (the touchAttachments that blob.analyze's update touches), oldest first.
    static List<Message> MessagesAttachedTo(SqliteSession session, Blob blob) =>
        DataQueries.Messages.WhereIds(session,
            [.. BlobRecords.AttachmentsOf(session, blob.Id).Where(attachment => attachment.RecordType == Message.ModelName).Select(attachment => attachment.RecordId)]);

    // `message[client_message_id]`, as message_params permits it.
    static string? ClientMessageId(ParamHash attributes) =>
        attributes["client_message_id"] is { } id ? RubyValues.ToS(id) : null;
}
