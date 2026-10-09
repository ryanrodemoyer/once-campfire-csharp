using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Variants;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>process_attachment</c> (reference/app/models/message/attachment.rb), once
/// <c>create_with_attachment!</c> has committed: <c>ensure_attachment_analyzed</c>, then
/// <c>process_attachment_thumbnail</c> — a video's preview
/// (<c>attachment.preview(format: :webp).processed</c>: the preview image, then the preview's webp
/// variant), or a representable image's <c>:thumb</c>
/// (<c>attachment.representation(:thumb).processed</c>). A plain file (neither variable nor
/// previewable, as this Campfire's libvips blocks make a bmp) has neither.
/// <para>
/// The processing only makes what's missing. The attachments it creates (the preview image, a
/// variant's image) enqueue <c>AnalyzeJob</c> for their blobs through the job seam:
/// <c>after_create_commit analyze_blob_later</c>, which the storage layer's attaches can't reach.
/// </para>
/// </summary>
static class MessageAttachmentProcessing
{
    public static async Task ProcessAsync(
        SqliteDatabase database,
        KeyGenerator keys,
        BlobStorage storage,
        DomainSeams seams,
        TimeProvider clock,
        string? host,
        DateTimeOffset now,
        Message message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(message);
        var blob = await database.ReadAsync(
            session => BlobRecords.FindAttachedBlob(session, Message.ModelName, message.Id, AttachmentNames.Attachment), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {message.Id}'s attachment is gone");
        // `ensure_attachment_analyzed`: `attachment&.analyze`.
        await MessagesWriteController.AnalyzeAsync(database, keys, storage, host, blob, now).ConfigureAwait(false);

        var media = new MediaProcessor(storage);
        var processor = new VariantProcessor(database, clock);
        if (blob.IsVideo)
        {
            var preview = Representable.Preview(blob, NamedVariants.VideoPreview);
            var (hadPreviewImage, hadVariant) = await database.ReadAsync(session =>
            {
                var image = BlobRecords.FindAttachedBlob(session, VariantProcessor.PreviewImageRecordType, blob.Id, VariantProcessor.PreviewImageName);
                return (image is not null,
                    image is not null && VariantRecords.Find(session, image.Id, Representable.Variant(image, preview.Variation).Variation.Digest) is not null);
            }, cancellationToken).ConfigureAwait(false);
            var presented = await processor.ProcessedAsync(preview, media.RenderPreviewImage, media.TransformVariant, cancellationToken).ConfigureAwait(false);
            if (!hadPreviewImage)
            {
                await EnqueueAnalysisAsync(database, seams, VariantProcessor.PreviewImageRecordType, blob.Id, VariantProcessor.PreviewImageName, cancellationToken).ConfigureAwait(false);
            }
            if (!hadVariant && presented is { IsAnalyzed: false })
            {
                seams.Jobs.Enqueue(new AnalyzeBlobJob(presented.Id));
            }
        }
        else if (Representable.IsRepresentable(blob))
        {
            var variant = Representable.Variant(blob, NamedVariants.MessageThumb);
            var hadRecord = await database.ReadAsync(
                session => VariantRecords.Find(session, blob.Id, variant.Variation.Digest) is not null, cancellationToken).ConfigureAwait(false);
            var image = await processor.ProcessedAsync(variant, media.TransformVariant, cancellationToken).ConfigureAwait(false);
            if (!hadRecord && image is { IsAnalyzed: false })
            {
                seams.Jobs.Enqueue(new AnalyzeBlobJob(image.Id));
            }
        }
    }

    // `analyze_blob_later` — `blob.analyze_later unless blob.analyzed?` — for an attachment that
    // just appeared.
    static async Task EnqueueAnalysisAsync(
        SqliteDatabase database, DomainSeams seams, string recordType, long recordId, string name, CancellationToken cancellationToken)
    {
        var blob = await database.ReadAsync(
            session => BlobRecords.FindAttachedBlob(session, recordType, recordId, name), cancellationToken).ConfigureAwait(false);
        if (blob is { IsAnalyzed: false })
        {
            seams.Jobs.Enqueue(new AnalyzeBlobJob(blob.Id));
        }
    }
}
