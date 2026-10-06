using System.Text.Json.Nodes;
using Campfire.Storage.Blobs;
using Campfire.Storage.Tests.Blobs;
using Campfire.Storage.Variants;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Variants;

/// <summary>
/// <c>processed</c>: variant records and preview images that exist, whether Rails or C# wrote them,
/// are reused; only missing ones are generated, once.
/// </summary>
public sealed class VariantProcessorTests : IDisposable
{
    static readonly DateTimeOffset Now = StorageFixture.Time("2026-10-05T12:34:56.789012Z");

    readonly ScratchStorage scratch = new();
    readonly VariantProcessor processor;
    int transforms;
    int renders;

    public VariantProcessorTests() => processor = new VariantProcessor(scratch.Database, new FixedClock(Now));

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<StoredVariant> Variants() => StorageVectors.Variants();

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_variant_record_the_reference_wrote_is_reused_not_regenerated(StoredVariant vector)
    {
        // The rows as Rails wrote them: source blob, variant record, and its image attachment.
        var source = await InsertBlob(SourceBlob(vector.SourceBlobId));
        var image = await InsertBlob(vector.Blob);
        await scratch.Database.WriteAsync(tx =>
        {
            tx.Session.Execute("INSERT INTO active_storage_variant_records (id, blob_id, variation_digest) VALUES (@id, @blob_id, @digest)",
                ("@id", vector.VariantRecord[0].GetInt64()), ("@blob_id", source.Id), ("@digest", vector.VariationDigest));
            BlobStorage.AttachOne(tx, image, VariantRecords.RecordType, vector.VariantRecord[0].GetInt64(), VariantRecords.ImageName, Now);
        }, Cancel);
        var variant = Representable.Variant(source, new Variation(TypedRuby.Transformations(vector.TransformationsTyped)));

        var processed = await processor.ProcessedAsync(variant, Transform, Cancel);

        Assert.Equal(image.Id, processed?.Id);
        Assert.Equal(vector.Blob.Key, processed?.Key);
        Assert.Equal(0, transforms);
    }

    [Fact]
    public async Task A_missing_variant_is_transformed_once_and_recorded_as_rails_records_it()
    {
        var source = await StageSource("moon.jpg", "image/jpeg");
        var thumb = Representable.Variant(source, NamedVariants.MessageThumb);

        var first = await processor.ProcessedAsync(thumb, Transform, Cancel);
        var again = await processor.ProcessedAsync(Representable.Variant(source, NamedVariants.MessageThumb), Transform, Cancel);

        Assert.Equal(1, transforms);
        Assert.NotNull(first);
        Assert.Equal(first.Id, again?.Id);
        Assert.Equal("moon.jpg", first.Filename.Value);
        var record = await scratch.Database.ReadAsync(s => VariantRecords.Find(s, source.Id, thumb.Variation.Digest), Cancel);
        Assert.NotNull(record);
        Assert.Equal("IBhrLAIapu+NCId+2Kz6EqUWRKY=", record.VariationDigest);
        var attachment = await scratch.Database.ReadAsync(s => BlobRecords.FindAttachment(s, VariantRecords.RecordType, record.Id, VariantRecords.ImageName), Cancel);
        Assert.Equal(first.Id, attachment?.BlobId);
        Assert.True(scratch.Storage.Service.Exist(first.Key));
    }

    [Fact]
    public async Task A_variant_recorded_while_transforming_wins_and_the_new_file_is_dropped()
    {
        var source = await StageSource("moon.jpg", "image/jpeg");
        var thumb = Representable.Variant(source, NamedVariants.MessageThumb);
        Blob? winner = null;
        string? loserKey = null;

        var processed = await processor.ProcessedAsync(thumb, variant =>
        {
            // Another request records the same variant first.
            using (var other = Transform(variant))
            {
                winner = scratch.Database.WriteAsync(tx =>
                {
                    var record = VariantRecords.CreateOrFind(tx, source.Id, variant.Variation.Digest, other, Now);
                    return VariantRecords.Image(tx.Session, record.Id);
                }, Cancel).GetAwaiter().GetResult();
            }
            var staged = Transform(variant);
            loserKey = staged.Blob.Key;
            return staged;
        }, Cancel);

        Assert.NotNull(winner);
        Assert.Equal(winner.Id, processed?.Id);
        Assert.False(scratch.Storage.Service.Exist(loserKey!));
        Assert.Null(await scratch.Database.ReadAsync(s => BlobRecords.FindBlobByKey(s, loserKey!), Cancel));
        Assert.Single(await scratch.Database.ReadAsync(s => VariantRecords.ForBlob(s, source.Id), Cancel));
    }

    [Fact]
    public async Task A_video_preview_renders_its_image_once_and_reuses_it_for_every_variation()
    {
        var video = await StageSource("alpha-centuri.mov", "video/quicktime");

        var preview = await processor.ProcessedAsync(Representable.Preview(video, NamedVariants.VideoPreview), Render, Transform, Cancel);
        var poster = await processor.ProcessedAsync(Representable.Preview(video, NamedVariants.VideoPoster), Render, Transform, Cancel);
        var posterAgain = await processor.ProcessedAsync(Representable.Preview(video, NamedVariants.VideoPoster), Render, Transform, Cancel);

        Assert.Equal(1, renders);
        Assert.Equal(2, transforms);
        Assert.Equal(poster?.Id, posterAgain?.Id);
        Assert.Equal("alpha-centuri.webp", preview?.Filename.Value);
        Assert.NotEqual(preview?.Id, poster?.Id);
        var image = await scratch.Database.ReadAsync(
            s => BlobRecords.FindAttachedBlob(s, VariantProcessor.PreviewImageRecordType, video.Id, VariantProcessor.PreviewImageName), Cancel);
        Assert.NotNull(image);
        var records = await scratch.Database.ReadAsync(s => VariantRecords.ForBlob(s, image.Id), Cancel);
        Assert.Equal(["IrCln/Mml8kDmpMni9j4/WNB7Uo=", "WRV0qldsxKR5cZNFjQ/qrXUXKAs="], records.Select(r => r.VariationDigest));
    }

    [Fact]
    public async Task An_empty_preview_presents_the_preview_image_itself()
    {
        var video = await StageSource("alpha-centuri.mov", "video/quicktime");

        var processed = await processor.ProcessedAsync(Representable.Preview(video, Transformations.Empty), Render, Transform, Cancel);

        Assert.Equal("alpha-centuri.jpg", processed?.Filename.Value);
        Assert.Equal(0, transforms);
    }

    public void Dispose() => scratch.Dispose();

    // Stand-ins for S03's image work: the source bytes, named and typed as the variant would be.
    StagedBlob Transform(VariantWithRecord variant)
    {
        transforms++;
        using var source = File.OpenRead(scratch.Storage.Service.PathFor(variant.Blob.Key));
        return scratch.Storage.Stage(source, variant.Filename, variant.ContentType, identify: false);
    }

    StagedBlob Render(Blob video)
    {
        renders++;
        using var frame = File.OpenRead(StorageFixture.Fixture("moon.jpg"));
        return scratch.Storage.Stage(frame, new Filename($"{video.Filename.Base}.jpg"), "image/jpeg", identify: false);
    }

    async Task<Blob> StageSource(string fixture, string contentType)
    {
        using var file = File.OpenRead(StorageFixture.Fixture(fixture));
        using var staged = scratch.Storage.Stage(file, new Filename(fixture), contentType, identify: false);
        return await scratch.Database.WriteAsync(tx => BlobStorage.Create(tx, staged, Now), Cancel);
    }

    async Task<Blob> InsertBlob(StoredBlob row)
    {
        var blob = StorageFixture.BlobFrom(row);
        var inserted = await scratch.Database.WriteAsync(tx => BlobRecords.InsertBlob(tx.Session,
            new NewBlob(blob.Key, blob.Filename, blob.ContentType, (JsonObject)JsonNode.Parse(row.Metadata)!, blob.ServiceName, blob.ByteSize, blob.Checksum),
            Now), Cancel);
        return inserted;
    }

    static StoredBlob SourceBlob(long id) =>
        StorageVectors.File.Messages.SelectMany(m => new[] { m.Blob, m.PreviewImage?.Blob })
            .Concat(StorageVectors.File.Avatars.Concat(StorageVectors.File.Logos).Select(i => i.Blob))
            .First(b => b?.Id == id)!;

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
