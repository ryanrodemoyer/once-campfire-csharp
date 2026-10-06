using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Tests.Blobs;
using Campfire.Storage.Tests.Variants;
using Campfire.Storage.Variants;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Media;

/// <summary>
/// The media golden set: every variant, preview image and analysis Rails produced for the storage
/// vectors (vectors/storage.json, vectors/storage/), made again from the same fixtures through
/// <see cref="MediaProcessor"/> and S02's <see cref="VariantProcessor"/>.
/// <para>
/// Filenames, content types and analyzed metadata must always match. The bytes depend on the
/// libvips and ffmpeg builds, so they are compared when the local versions are the ones that made
/// the vectors (the pinned toolchain, P02's image); otherwise that comparison is skipped and the
/// test reports why. Set <c>CAMPFIRE_REQUIRE_MEDIA_VECTORS=1</c> to fail instead of skipping, as the
/// pinned image does.
/// </para>
/// </summary>
public sealed class MediaGoldenTests : IDisposable
{
    const long previewImageId = 8;

    readonly ScratchStorage scratch = new();
    readonly BlobStorage storage;
    readonly MediaProcessor media;
    readonly VariantProcessor processor;

    public MediaGoldenTests()
    {
        storage = BlobStorage.Local(scratch.Root, StorageFixture.Keys, Marcel.Identify);
        media = new MediaProcessor(storage);
        processor = new VariantProcessor(scratch.Database, TimeProvider.System);
    }

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<StoredVariant> Variants() => StorageVectors.Variants();

    public static TheoryData<string> Originals() => new(OriginalsById().Values.Select(o => o.Blob.Filename + "#" + o.Blob.Id));

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Variant_equals_the_reference(StoredVariant vector)
    {
        Toolchain.RequireLibVips();
        var source = await StageSource(vector.SourceBlobId);
        var variant = Representable.Variant(source, new Variation(TypedRuby.Transformations(vector.TransformationsTyped)));

        var image = await processor.ProcessedAsync(variant, media.TransformVariant, Cancel);

        Assert.NotNull(image);
        Assert.Equal(vector.Blob.Filename, image.Filename.Value);
        Assert.Equal(vector.Blob.ContentType, image.ContentType);
        Assert.Equal("""{"identified":true}""", RailsJson.Encode(image.Metadata));
        var analyzed = await media.AnalyzeAsync(scratch.Database, image, Cancel);
        Assert.Equal(vector.Blob.Metadata, RailsJson.Encode(analyzed.Metadata));
        Toolchain.CompareImageBytes(vector.File, vector.Blob, storage.Service.PathFor(image.Key));
    }

    [Fact]
    public async Task Preview_image_equals_the_reference()
    {
        Toolchain.RequireFfmpeg();
        var vector = StorageVectors.File.Messages.Single(m => m.PreviewImage is not null);
        var video = await StageSource(vector.Blob.Id);
        var preview = Representable.Preview(video, Transformations.Empty);

        var image = await processor.ProcessedAsync(preview, media.RenderPreviewImage, media.TransformVariant, Cancel);

        Assert.NotNull(image);
        var expected = vector.PreviewImage!.Blob;
        Assert.Equal(expected.Filename, image.Filename.Value);
        Assert.Equal(expected.ContentType, image.ContentType);
        Assert.Equal("""{"identified":true}""", RailsJson.Encode(image.Metadata));
        Toolchain.RequireLibVips();
        var analyzed = await media.AnalyzeAsync(scratch.Database, image, Cancel);
        Assert.Equal(expected.Metadata, RailsJson.Encode(analyzed.Metadata));
        Toolchain.CompareVideoBytes(vector.PreviewImage.File, expected, storage.Service.PathFor(image.Key));
    }

    [Theory]
    [MemberData(nameof(Originals))]
    public async Task Analysis_of_every_upload_equals_the_reference(string label)
    {
        var original = OriginalsById().Values.Single(o => o.Blob.Filename + "#" + o.Blob.Id == label);
        if (original.Blob.ContentType.StartsWith("video", StringComparison.Ordinal))
        {
            Toolchain.RequireFfmpeg();
        }
        else
        {
            Toolchain.RequireLibVips();
        }
        var blob = await StageSource(original.Blob.Id);

        var analyzed = await media.AnalyzeAsync(scratch.Database, blob, Cancel);

        Assert.Equal(original.Blob.Metadata, RailsJson.Encode(analyzed.Metadata));
        var stored = await scratch.Database.ReadAsync(session => BlobRecords.FindBlob(session, blob.Id), Cancel);
        Assert.Equal(original.Blob.Metadata, RailsJson.Encode(stored!.Metadata));
    }

    /// <summary>Uploads a reference blob's file as Rails did: its filename, its declared type, identified.</summary>
    async Task<Blob> StageSource(long id)
    {
        var original = OriginalsById()[id];
        using var staged = storage.Stage(new MemoryStream(original.Bytes()), new Filename(original.Blob.Filename), original.Blob.ContentType);
        Assert.Equal(original.Blob.ContentType, staged.Blob.ContentType);
        Assert.Equal(original.Blob.Checksum, staged.Blob.Checksum);
        return await scratch.Database.WriteAsync(tx => BlobStorage.Create(tx, staged, DateTimeOffset.UtcNow), Cancel);
    }

    /// <summary>Every blob the reference made a variant from or analyzed, with where its bytes are.</summary>
    static Dictionary<long, Original> OriginalsById()
    {
        var file = StorageVectors.File;
        var originals = file.Messages.Select(m => new Original(m.Blob, () => File.ReadAllBytes(StorageFixture.Fixture(m.Fixture))))
            .Concat(file.Avatars.Concat(file.Logos).Select(i => new Original(i.Blob, () => File.ReadAllBytes(StorageFixture.Fixture(i.Fixture)))))
            .ToDictionary(o => o.Blob.Id);
        var preview = file.Messages.Single(m => m.PreviewImage is not null).PreviewImage!;
        Assert.Equal(previewImageId, preview.Blob.Id);
        originals[preview.Blob.Id] = new Original(preview.Blob, () => StorageVectors.ReadFile(preview.File));
        return originals;
    }

    sealed record Original(StoredBlob Blob, Func<byte[]> Bytes);

    public void Dispose() => scratch.Dispose();
}
