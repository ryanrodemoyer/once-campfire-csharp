using Campfire.Data.MessageAttachments;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;
using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;

namespace Campfire.Web.Controllers;

/// <summary>
/// The Active Storage jobs the attachment flow enqueues (gem jobs, not app jobs), performed:
/// <c>ActiveStorage::AnalyzeJob#perform(blob)</c> is <c>blob.analyze</c>
/// (<see cref="MessagesWriteController.AnalyzeAsync"/>), and <c>ActiveStorage::PurgeJob#perform(blob)</c>
/// is <c>blob.purge</c> — the blob's row goes, with its variant records and its own preview image
/// (whose blobs are purged later in turn), unless an attachment still refers to it, and once the
/// transaction commits its files do too.
/// <para>
/// Register with the <see cref="JobRunner"/>, as RemoveBannedContent does; nothing wires it up yet,
/// and like every job in the port these are best-effort.
/// </para>
/// </summary>
public sealed class MessageAttachmentJobs(
    SqliteDatabase database,
    BlobStorage storage,
    KeyGenerator keys,
    TimeProvider clock,
    string? host = null)
{
    JobRunner? runner;

    public void RegisterWith(JobRunner runner, JobQueueLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        this.runner = runner;
        runner.Register<AnalyzeBlobJob>(PerformAsync, limits);
        runner.Register<PurgeBlobJob>(PerformAsync, limits);
    }

    // `AnalyzeJob.perform(blob)`: `blob.analyze`. A blob that's gone is discarded
    // (`discard_on ActiveRecord::RecordNotFound`).
    public async Task PerformAsync(AnalyzeBlobJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var blob = await database.ReadAsync(session => BlobRecords.FindBlob(session, job.BlobId), cancellationToken).ConfigureAwait(false);
        if (blob is not null)
        {
            await MessagesWriteController.AnalyzeAsync(database, keys, storage, host, blob, clock.GetUtcNow()).ConfigureAwait(false);
        }
    }

    // `PurgeJob.perform(blob)`: `blob.purge` — `destroy` (a blob an attachment still refers to
    // raises `ActiveRecord::InvalidForeignKey` in `before_destroy`, which `purge` rescues by doing
    // nothing) then, for the destroyed row, `delete` (the file, and an image's variant files).
    public async Task PerformAsync(PurgeBlobJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var blob = await database.ReadAsync(session => BlobRecords.FindBlob(session, job.BlobId), cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return; // `discard_on ActiveRecord::RecordNotFound`
        }
        await database.WriteAsync(transaction =>
        {
            var session = transaction.Session;
            if (BlobRecords.AttachmentsOf(session, blob.Id).Count > 0)
            {
                return false;
            }
            // `before_destroy { variant_records.destroy_all }` (track_variants): each record's image
            // (`has_one_attached :image`, dependent: :purge_later) goes with it, its blob purged later.
            foreach (var record in VariantRecords.ForBlob(session, blob.Id))
            {
                if (BlobRecords.FindAttachment(session, VariantRecords.RecordType, record.Id, VariantRecords.ImageName) is { } image)
                {
                    BlobRecords.DeleteAttachment(session, image.Id);
                    transaction.AfterCommit(_ => runner?.Enqueue(new PurgeBlobJob(image.BlobId)));
                }
                VariantRecords.Delete(session, record.Id);
            }
            // The blob's own preview image (`has_one_attached :preview_image` on
            // ActiveStorage::Blob, dependent: :purge_later): a video's first frame.
            if (BlobRecords.FindAttachment(session, VariantProcessor.PreviewImageRecordType, blob.Id, VariantProcessor.PreviewImageName) is { } previewImage)
            {
                BlobRecords.DeleteAttachment(session, previewImage.Id);
                transaction.AfterCommit(_ => runner?.Enqueue(new PurgeBlobJob(previewImage.BlobId)));
            }
            if (!BlobRecords.DeleteBlobIfUnattached(session, blob.Id))
            {
                return false;
            }
            transaction.AfterCommit(_ => storage.DeleteFiles(blob));
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}
