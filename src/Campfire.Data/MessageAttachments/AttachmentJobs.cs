using Campfire.Data.Events;

namespace Campfire.Data.MessageAttachments;

/// <summary>
/// <c>ActiveStorage::AnalyzeJob.perform_later(blob)</c>, as the gem's attachment callbacks enqueue
/// it (<c>ActiveStorage::Attachment.after_create_commit :analyze_blob_later</c>: <c>blob.analyze_later
/// unless blob.analyzed?</c>; activestorage/app/jobs/active_storage/analyze_job.rb): analyzes a
/// newly attached blob. Performed by M10's <c>MessageAttachmentJobs</c>.
/// </summary>
public sealed record AnalyzeBlobJob(long BlobId) : Job
{
    public override string ClassName => "ActiveStorage::AnalyzeJob";

    public override IReadOnlyList<long> ArgumentIds => [BlobId];
}

/// <summary>
/// <c>ActiveStorage::PurgeJob.perform_later(blob)</c>, as <c>blob.purge_later</c> enqueues it — from
/// <c>ActiveStorage::Attachment.after_destroy_commit :purge_dependent_blob_later</c> when the
/// record's <c>has_one_attached</c> runs <c>dependent: :purge_later</c>
/// (activestorage/app/jobs/active_storage/purge_job.rb): deletes the blob's row and, once nothing
/// refers to it, its files. Performed by M10's <c>MessageAttachmentJobs</c>.
/// </summary>
public sealed record PurgeBlobJob(long BlobId) : Job
{
    public override string ClassName => "ActiveStorage::PurgeJob";

    public override IReadOnlyList<long> ArgumentIds => [BlobId];
}
