using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;

namespace Campfire.Storage.Media;

/// <summary>
/// The file work behind representations and analysis, over one <see cref="BlobStorage"/>:
/// S02's <see cref="VariantTransformer"/> and <see cref="PreviewImageRenderer"/>, and <c>blob.analyze</c>.
/// Each works on a local copy of the blob (<c>blob.open</c>) and stages its output as Rails attaches
/// it, so <see cref="VariantProcessor"/> can record it. The storage should identify content with
/// <see cref="Marcel.Identify"/>, as Rails does when it attaches the output.
/// </summary>
/// <param name="storage">Where blobs are read and outputs are staged.</param>
/// <param name="toolTimeout">
/// How long ffmpeg and ffprobe may run before they're killed. Rails sets no limit, which is the
/// default here too.
/// </param>
public sealed class MediaProcessor(BlobStorage storage, TimeSpan? toolTimeout = null)
{
    /// <summary>
    /// <c>VariantWithRecord#transform_blob</c>: <c>variation.transform(input)</c>, staged as the
    /// variant's filename (<c>&lt;base&gt;.&lt;format&gt;</c>) and content type, which Marcel still
    /// identifies, as <c>create_or_find_record(image: { io:, filename:, content_type: })</c> does.
    /// </summary>
    public StagedBlob TransformVariant(VariantWithRecord variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var format = variant.Variation.Format;
        using var input = BlobTempfile.Open(storage, variant.Blob);
        using var output = BlobTempfile.Create("image_processing", $".{format}");
        ImageTransformer.Transform(input.Path, variant.Variation, output.Path);
        using var transformed = File.OpenRead(output.Path);
        return storage.Stage(transformed, variant.Filename, variant.ContentType);
    }

    /// <summary>
    /// <c>Preview#process</c>'s <c>previewer.preview</c>: the video's relevant frame, staged as
    /// <c>&lt;base&gt;.jpg</c>, <c>image/jpeg</c>, for <c>preview_image.attach</c>.
    /// </summary>
    public StagedBlob RenderPreviewImage(Blob video)
    {
        ArgumentNullException.ThrowIfNull(video);
        using var input = BlobTempfile.Open(storage, video);
        using var output = BlobTempfile.Create("ActiveStorage-", "");
        using (var frame = new FileStream(output.Path, FileMode.Truncate, FileAccess.Write))
        {
            VideoPreviewer.DrawRelevantFrame(input.Path, frame, timeout: toolTimeout);
        }
        using var image = File.OpenRead(output.Path);
        return storage.Stage(image, new Filename($"{video.Filename.Base}.jpg"), "image/jpeg");
    }

    /// <summary>
    /// The metadata <c>blob.analyze</c> saves (<see cref="BlobAnalyzer.Analyze"/>), read from a
    /// local copy of the blob. Save it with <see cref="AnalyzeAsync"/>, or
    /// <see cref="BlobRecords.UpdateMetadata"/> in the caller's transaction.
    /// </summary>
    public JsonObject AnalyzedMetadata(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (BlobAnalyzer.For(blob) == AnalyzerKind.Null)
        {
            return BlobAnalyzer.Analyze(blob, "", toolTimeout);
        }
        using var file = BlobTempfile.Open(storage, blob);
        return BlobAnalyzer.Analyze(blob, file.Path, toolTimeout);
    }

    /// <summary>
    /// <c>blob.analyze</c>: <c>update!(metadata: metadata.merge(extract_metadata_via_analyzer))</c>.
    /// The file is read outside the writer; returns the blob with its new metadata.
    /// </summary>
    public async Task<Blob> AnalyzeAsync(SqliteDatabase database, Blob blob, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var metadata = AnalyzedMetadata(blob);
        await database.WriteAsync(tx => BlobRecords.UpdateMetadata(tx.Session, blob.Id, metadata), cancellationToken).ConfigureAwait(false);
        return blob with { Metadata = metadata };
    }
}
