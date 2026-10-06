using Campfire.Data.Sqlite;
using Campfire.Storage.Blobs;

namespace Campfire.Storage.Variants;

/// <summary>
/// Transforms <paramref name="variant"/>'s blob and stages the result as <c>VariantWithRecord#transform_blob</c>
/// does: named <see cref="VariantWithRecord.Filename"/>, typed <see cref="VariantWithRecord.ContentType"/>
/// (Marcel still identifies the bytes). The image work is S03's.
/// </summary>
public delegate StagedBlob VariantTransformer(VariantWithRecord variant);

/// <summary>A previewer's <c>preview</c>: the video's first frame, staged as the preview image. S03's.</summary>
public delegate StagedBlob PreviewImageRenderer(Blob video);

/// <summary>
/// <c>representation.processed</c>. An existing variant record (or preview image) is reused as it is,
/// whoever wrote it: only a missing one is generated. The file work happens outside the writer, and
/// a variant that another request recorded in the meantime wins, as <c>create_or_find_by!</c> lets it.
/// </summary>
public sealed class VariantProcessor(SqliteDatabase database, TimeProvider clock)
{
    public const string PreviewImageRecordType = "ActiveStorage::Blob";
    public const string PreviewImageName = "preview_image";

    /// <summary>
    /// <c>variant.processed.image</c>: the variant's file, transforming the blob only when no record
    /// exists for its digest. Null when the record exists but its image is gone, as in Rails.
    /// </summary>
    public async Task<Blob?> ProcessedAsync(VariantWithRecord variant, VariantTransformer transform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(transform);
        var (blobId, digest) = (variant.Blob.Id, variant.Variation.Digest);
        var existing = await database.ReadAsync(session => Image(session, blobId, digest), cancellationToken).ConfigureAwait(false);
        if (existing.Recorded)
        {
            return existing.Image;
        }
        using var staged = transform(variant);
        return await database.WriteAsync(tx =>
        {
            var record = VariantRecords.CreateOrFind(tx, blobId, digest, staged, clock.GetUtcNow());
            return VariantRecords.Image(tx.Session, record.Id);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>preview.processed</c>, returning what it presents: the preview image's variant, or the
    /// preview image itself when the variation is empty. The first frame is rendered only when the
    /// video has no <c>preview_image</c> yet.
    /// </summary>
    public async Task<Blob?> ProcessedAsync(
        Preview preview, PreviewImageRenderer render, VariantTransformer transform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(render);
        var image = await PreviewImageAsync(preview.Blob, render, cancellationToken).ConfigureAwait(false);
        return preview.HasVariant ? await ProcessedAsync(preview.VariantOf(image), transform, cancellationToken).ConfigureAwait(false) : image;
    }

    /// <summary><c>blob.preview_image</c>, rendering and attaching it first if it isn't attached.</summary>
    async Task<Blob> PreviewImageAsync(Blob video, PreviewImageRenderer render, CancellationToken cancellationToken)
    {
        var attached = await database.ReadAsync(session => BlobRecords.FindAttachedBlob(session, PreviewImageRecordType, video.Id, PreviewImageName),
            cancellationToken).ConfigureAwait(false);
        if (attached is not null)
        {
            return attached;
        }
        using var staged = render(video);
        // image.attach(attachable): a preview image attached meanwhile is replaced, as in Rails.
        return await database.WriteAsync(tx =>
            BlobStorage.AttachOne(tx, staged, PreviewImageRecordType, video.Id, PreviewImageName, clock.GetUtcNow()).Blob,
            cancellationToken).ConfigureAwait(false);
    }

    static (bool Recorded, Blob? Image) Image(SqliteSession session, long blobId, string digest) =>
        VariantRecords.Find(session, blobId, digest) is { } record ? (true, VariantRecords.Image(session, record.Id)) : (false, null);
}
