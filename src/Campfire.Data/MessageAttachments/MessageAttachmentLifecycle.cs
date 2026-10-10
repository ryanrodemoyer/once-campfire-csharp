using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Data.MessageAttachments;

/// <summary>
/// The attachment half of a message's writes (reference/app/models/message/attachment.rb and the
/// Active Storage gem behind it), at the Data layer's reach: the blob and attachment rows in the
/// message's own transaction, and the jobs their commits enqueue through the domain seams. The file
/// work — staging the upload, the analysis, the thumbnail and the video preview — is Storage's and
/// the Web controller's; <c>StagedBlob</c> is a Storage type, so an upload crosses as
/// <see cref="StagedUpload"/>'s values.
/// <para>
/// Destroying a message with a file is shared code: MessagesWriteController's hook calls it, and
/// RemoveBannedContentJob's destroys should too, so a banned person's messages purge their files
/// the same way.
/// </para>
/// </summary>
public static class MessageAttachmentLifecycle
{
    /// <summary>
    /// <c>room.messages.create_with_attachment!(…)</c> (message/attachment.rb) with an uploaded
    /// file: the message (<see cref="MessageLifecycle.Create"/>), then the blob and attachment rows
    /// in the message's transaction (<c>Attached::Changes::CreateOne#save</c>, autosaved inside the
    /// save). The attachment's <c>belongs_to :record, touch: true</c> touches the message (then its
    /// room), and after the commit <c>analyze_blob_later</c> enqueues <see cref="AnalyzeBlobJob"/>
    /// for the new, unanalyzed blob. The staged file is the caller's to keep once the transaction
    /// commits, as <c>CreateOne#upload</c> keeps it after commit.
    /// </summary>
    public static Message CreateWithAttachment(
        WriteTransaction transaction,
        DomainSeams seams,
        long roomId,
        long creatorId,
        string? clientMessageId,
        string? bodyHtml,
        string plainTextBody,
        StagedUpload upload,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(upload);
        var session = transaction.Session;
        var message = MessageLifecycle.Create(transaction, seams, roomId, creatorId, clientMessageId, bodyHtml, plainTextBody, now);
        var blob = Blobs.Create(session, upload.Key, upload.Filename, upload.ContentType, RailsJson.Encode(upload.Metadata), upload.ServiceName, upload.ByteSize, upload.Checksum, now);
        Attach(session, message.Id, blob.Id, now);
        MessageLifecycle.Touch(transaction, message, plainTextBody, now);
        transaction.AfterCommit(_ => seams.Jobs.Enqueue(new AnalyzeBlobJob(blob.Id)));
        return message;
    }

    /// <summary>
    /// The same create with an existing blob (a signed id, <c>Blob.find_signed!</c>): no blob row to
    /// write and no file to keep, and no <see cref="AnalyzeBlobJob"/> when the blob was already
    /// analyzed — as an existing install's blobs are.
    /// </summary>
    public static Message CreateWithExistingBlob(
        WriteTransaction transaction,
        DomainSeams seams,
        long roomId,
        long creatorId,
        string? clientMessageId,
        string? bodyHtml,
        string plainTextBody,
        long blobId,
        bool blobAnalyzed,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        var session = transaction.Session;
        var message = MessageLifecycle.Create(transaction, seams, roomId, creatorId, clientMessageId, bodyHtml, plainTextBody, now);
        Attach(session, message.Id, blobId, now);
        MessageLifecycle.Touch(transaction, message, plainTextBody, now);
        if (!blobAnalyzed)
        {
            transaction.AfterCommit(_ => seams.Jobs.Enqueue(new AnalyzeBlobJob(blobId)));
        }
        return message;
    }

    /// <summary>
    /// <c>message.destroy</c> with an attached file (messages_controller.rb destroy): the
    /// attachment row goes in the message's transaction (<c>has_one :attachment_attachment,
    /// dependent: :destroy</c>) and, after the commit, its blob is purged later
    /// (<c>ActiveStorage::Attachment.after_destroy_commit :purge_dependent_blob_later</c> —
    /// <c>blob.purge_later</c>). Everything else is <see cref="MessageLifecycle.Destroy"/>'s. The
    /// blob's row and files stay until the job runs them down, as Rails leaves them.
    /// </summary>
    public static void DestroyWithAttachment(WriteTransaction transaction, IJobQueue jobs, Message message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(message);
        var session = transaction.Session;
        if (Attachments.For(session, Message.ModelName, message.Id, AttachmentNames.Attachment) is { } attachment)
        {
            Attachments.Delete(session, attachment.Id);
            transaction.AfterCommit(_ => jobs.Enqueue(new PurgeBlobJob(attachment.BlobId)));
        }
        MessageLifecycle.Destroy(transaction, message, now);
    }

    public static void DestroyWithAttachment(WriteTransaction transaction, DomainSeams seams, Message message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(seams);
        DestroyWithAttachment(transaction, seams.Jobs, message, now);
    }

    /// <summary>
    /// <c>attachment.touch</c> where the attachment's record has an <c>updated_at</c>, as
    /// <c>blob.analyze</c>'s <c>after_update :touch_attachments</c> touches it: the message, then
    /// its room, re-indexed after the commit.
    /// </summary>
    public static void Touch(WriteTransaction transaction, Message message, string plainTextBody, DateTimeOffset now) =>
        MessageLifecycle.Touch(transaction, message, plainTextBody, now);

    static void Attach(SqliteSession session, long messageId, long blobId, DateTimeOffset now) =>
        Attachments.Create(session, Message.ModelName, messageId, AttachmentNames.Attachment, blobId, now);
}

/// <summary>
/// A staged upload as Data writes its blob row: everything <c>Blob.build_after_unfurling</c>
/// measured (the checksum, the size and the identified content type) plus the key Stage picked and
/// the metadata it set (<c>{"identified":true}</c>). <c>StagedBlob</c> is a Storage type; Data sits
/// below it, so the upload crosses as these values.
/// </summary>
public sealed record StagedUpload(
    string Key,
    string Filename,
    string? ContentType,
    JsonObject Metadata,
    string ServiceName,
    long ByteSize,
    string? Checksum);
