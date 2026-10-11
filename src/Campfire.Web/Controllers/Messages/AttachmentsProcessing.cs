using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Variants;
using Campfire.Web.Helpers;

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
        BlobStorage storage,
        DomainSeams seams,
        TimeProvider clock,
        DateTimeOffset now,
        Message message,
        CancellationToken cancellationToken) =>
        await ProcessAsync(database, storage, seams, clock, now, message, NamedVariants.ThumbnailMaxPixels, cancellationToken).ConfigureAwait(false);

    public static async Task ProcessAsync(
        SqliteDatabase database,
        BlobStorage storage,
        DomainSeams seams,
        TimeProvider clock,
        DateTimeOffset now,
        Message message,
        long maxPixels,
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
        await MessagesWriteController.AnalyzeAsync(database, storage, blob, now).ConfigureAwait(false);

        blob = await database.ReadAsync(session => BlobRecords.FindBlob(session, blob.Id), cancellationToken).ConfigureAwait(false) ?? blob;

        if (TooManyPixelsToPreview(blob, maxPixels))
        {
            return;
        }

        var media = new MediaProcessor(storage);
        var processor = new VariantProcessor(database, clock);
        try
        {
            if (blob.IsVideo)
            {
                var preview = Representable.Preview(blob, NamedVariants.VideoPoster);
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
        catch (MediaException)
        {
            // A file that ffmpeg or libvips can't decode is still the message: post it without a preview.
            // Rails.logger.warn "Posted #{attachment.filename} without a preview: #{error.class}: #{error.message.lines.first&.chomp}"
        }
    }

    /// <summary>
    /// <c>too_many_pixels_to_preview?</c>: Decoding a picture, or a video's frame, costs in proportion to its
    /// pixels, however small the file.
    /// </summary>
    public static bool TooManyPixelsToPreview(Blob blob, long? maxPixels = null)
    {
        var limit = maxPixels ?? NamedVariants.ThumbnailMaxPixels;
        if (blob.IsImage || blob.IsVideo)
        {
            if (blob.Metadata.TryGetPropertyValue("width", out var widthNode) &&
                blob.Metadata.TryGetPropertyValue("height", out var heightNode) &&
                widthNode is not null && heightNode is not null)
            {
                var width = RubyNumber.From(widthNode)?.ToDouble();
                var height = RubyNumber.From(heightNode)?.ToDouble();
                if (width is { } w && height is { } h)
                {
                    return w * h > limit;
                }
            }
            return true;
        }
        return false;
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
