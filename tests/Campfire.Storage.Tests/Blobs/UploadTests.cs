using Campfire.RailsCompat.Params;
using Campfire.Storage.Blobs;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Blobs;

/// <summary>Uploading and attaching: rows, checksums, keys and files as the reference writes them.</summary>
public sealed class UploadTests : IDisposable
{
    static readonly DateTimeOffset Now = StorageFixture.Time("2026-10-05T12:34:56.789012Z");

    readonly ScratchStorage scratch = new();

    BlobStorage Storage => scratch.Storage;

    public static TheoryData<StoredMessageAttachment> Messages() => StorageVectors.Messages();

    [Theory]
    [MemberData(nameof(Messages))]
    public async Task Attaching_an_upload_writes_the_rows_and_file_the_reference_does(StoredMessageAttachment vector)
    {
        using var upload = UploadedFile.FromBytes(vector.Fixture, vector.DeclaredType, File.ReadAllBytes(StorageFixture.Fixture(vector.Fixture)));
        using var staged = Storage.Stage(upload);

        var attached = await scratch.Database.WriteAsync(tx =>
            BlobStorage.AttachOne(tx, staged, "Message", 1, "attachment", Now), TestContext.Current.CancellationToken);

        var blob = await scratch.Database.ReadAsync(session => BlobRecords.FindBlob(session, attached.Blob.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(blob);
        Assert.Equal(attached.Blob, blob with { Metadata = attached.Blob.Metadata });
        Assert.Equal(vector.Blob.Checksum, blob.Checksum);
        Assert.Equal(vector.Blob.ByteSize, blob.ByteSize);
        Assert.Equal(vector.Blob.Filename, blob.Filename.Value);
        Assert.Equal(vector.Blob.ContentType, blob.ContentType);
        Assert.Equal(vector.Blob.ServiceName, blob.ServiceName);
        Assert.Equal("""{"identified":true}""", await RawMetadata(blob.Id));
        Assert.Matches("^[0-9a-z]{28}$", blob.Key);
        Assert.Equal(Now, blob.CreatedAt);

        var path = Storage.Service.PathFor(blob.Key);
        Assert.Equal(Path.Combine(scratch.Root, blob.Key[..2], blob.Key[2..4], blob.Key), path);
        Assert.Equal(File.ReadAllBytes(StorageFixture.Fixture(vector.Fixture)), File.ReadAllBytes(path));
        Storage.VerifyIntegrity(blob);

        Assert.Equal(new Attachment(attached.Attachment.Id, "attachment", "Message", 1, blob.Id, Now), attached.Attachment);
        Assert.Equal(attached.Attachment, await scratch.Database.ReadAsync(s => BlobRecords.FindAttachment(s, "Message", 1, "attachment"), TestContext.Current.CancellationToken));
        Assert.Null(attached.ReplacedBlobId);
        Assert.True(staged.Kept);
    }

    [Fact]
    public async Task A_rolled_back_attach_leaves_no_rows_and_no_file()
    {
        string key;
        using (var staged = Stage("moon.jpg"))
        {
            key = staged.Blob.Key;
            Assert.True(Storage.Service.Exist(key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scratch.Database.WriteAsync(tx =>
            {
                BlobStorage.AttachOne(tx, staged, "Message", 1, "attachment", Now);
                throw new InvalidOperationException("the message failed to save");
            }, TestContext.Current.CancellationToken));
            Assert.False(staged.Kept);
        }

        Assert.False(Storage.Service.Exist(key));
        Assert.Equal(0, await Count("active_storage_blobs"));
        Assert.Equal(0, await Count("active_storage_attachments"));
    }

    [Fact]
    public async Task Attaching_again_replaces_the_attachment_and_reports_the_old_blob_for_purging()
    {
        var touches = 0;
        using var first = Stage("moon.jpg");
        using var second = Stage("earth.png");

        var original = await scratch.Database.WriteAsync(tx => BlobStorage.AttachOne(tx, first, "User", 7, "avatar", Now, _ => touches++),
            TestContext.Current.CancellationToken);
        var replacement = await scratch.Database.WriteAsync(tx => BlobStorage.AttachOne(tx, second, "User", 7, "avatar", Now, _ => touches++),
            TestContext.Current.CancellationToken);

        Assert.Equal(original.Blob.Id, replacement.ReplacedBlobId);
        Assert.Equal(3, touches);
        Assert.Equal(1, await Count("active_storage_attachments"));
        var current = await scratch.Database.ReadAsync(s => BlobRecords.FindAttachedBlob(s, "User", 7, "avatar"), TestContext.Current.CancellationToken);
        Assert.Equal(replacement.Blob.Id, current!.Id);

        // The purge the caller enqueues: the old blob is no longer attached, so its row and file go.
        Assert.True(await scratch.Database.WriteAsync(tx => BlobRecords.DeleteBlobIfUnattached(tx.Session, original.Blob.Id), TestContext.Current.CancellationToken));
        Storage.DeleteFiles(original.Blob);
        Assert.False(Storage.Service.Exist(original.Blob.Key));
        Assert.False(await scratch.Database.WriteAsync(tx => BlobRecords.DeleteBlobIfUnattached(tx.Session, replacement.Blob.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Attaching_an_existing_blob_twice_changes_nothing()
    {
        using var staged = Stage("moon.jpg");
        var blob = await scratch.Database.WriteAsync(tx => BlobStorage.Create(tx, staged, Now), TestContext.Current.CancellationToken);

        var first = await scratch.Database.WriteAsync(tx => BlobStorage.AttachOne(tx, blob, "Account", 1, "logo", Now), TestContext.Current.CancellationToken);
        var again = await scratch.Database.WriteAsync(tx => BlobStorage.AttachOne(tx, blob, "Account", 1, "logo", Now), TestContext.Current.CancellationToken);

        Assert.Equal(first.Attachment, again.Attachment);
        Assert.Null(again.ReplacedBlobId);
        Assert.Single(await scratch.Database.ReadAsync(s => BlobRecords.AttachmentsOf(s, blob.Id), TestContext.Current.CancellationToken));
        Assert.Equal(blob, await scratch.Database.ReadAsync(s => BlobRecords.FindBlobByKey(s, blob.Key), TestContext.Current.CancellationToken) is { } found ? found with { Metadata = blob.Metadata } : null);
    }

    [Fact]
    public void Content_type_is_identified_unless_a_declared_type_is_trusted()
    {
        var seen = new List<string?>();
        var storage = new BlobStorage(Storage.Service, Storage.Verifier, (content, name, declared) =>
        {
            seen.Add($"{name}:{declared}:{content.ReadByte()}");
            return "image/identified";
        });

        using var identified = storage.Stage(new MemoryStream([1, 2]), new Filename(" a/b.png "), "image/png");
        using var trusted = storage.Stage(new MemoryStream([1, 2]), new Filename("a.png"), "image/png", identify: false);
        using var undeclared = storage.Stage(new MemoryStream([3]), new Filename("c"), null, identify: false);

        Assert.Equal("image/identified", identified.Blob.ContentType);
        Assert.Equal("image/png", trusted.Blob.ContentType);
        Assert.Equal("image/identified", undeclared.Blob.ContentType);
        Assert.Equal(["a-b.png:image/png:1", "c::3"], seen);
        Assert.Equal(" a/b.png ", identified.Blob.Filename.Value);
    }

    [Fact]
    public void Upload_with_a_wrong_checksum_is_deleted()
    {
        var service = Storage.Service;
        service.Upload("checkedkey0000000000000000000", new MemoryStream("bytes"u8.ToArray()), BlobKey.Checksum("bytes"u8));
        Assert.Equal("bytes"u8.ToArray(), service.Download("checkedkey0000000000000000000"));

        Assert.Throws<BlobIntegrityException>(() =>
            service.Upload("mismatched000000000000000000", new MemoryStream("bytes"u8.ToArray()), BlobKey.Checksum("other"u8)));
        Assert.False(service.Exist("mismatched000000000000000000"));
        Assert.Throws<BlobFileNotFoundException>(() => service.Download("mismatched000000000000000000"));
        service.Delete("mismatched000000000000000000");
    }

    [Fact]
    public async Task A_corrupted_file_fails_verification()
    {
        using var staged = Stage("moon.jpg");
        var blob = await scratch.Database.WriteAsync(tx => BlobStorage.Create(tx, staged, Now), TestContext.Current.CancellationToken);
        File.AppendAllText(Storage.Service.PathFor(blob.Key), "x");

        Assert.Throws<BlobIntegrityException>(() => Storage.VerifyIntegrity(blob));
    }

    [Fact]
    public void Deleting_an_image_removes_legacy_variants_too()
    {
        var service = Storage.Service;
        var image = new Blob(1, "imagekey00000000000000000000", new Filename("a.png"), "image/png", [], "local", 1, null, Now);
        service.Upload(image.Key, new MemoryStream([1]));
        service.Upload($"variants/{image.Key}/digest", new MemoryStream([2]));
        service.Upload("variants/otherkey/digest", new MemoryStream([3]));

        Storage.DeleteFiles(image);

        Assert.False(service.Exist(image.Key));
        Assert.False(service.Exist($"variants/{image.Key}/digest"));
        Assert.True(service.Exist("variants/otherkey/digest"));
    }

    [Fact]
    public void Keys_are_28_random_base36_characters()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => BlobKey.Generate()).ToList();

        Assert.All(keys, key => Assert.Matches("^[0-9a-z]{28}$", key));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(36, string.Concat(keys).Distinct().Count());
    }

    StagedBlob Stage(string fixture)
    {
        using var file = File.OpenRead(StorageFixture.Fixture(fixture));
        return Storage.Stage(file, new Filename(fixture), null);
    }

    Task<long> Count(string table) =>
        scratch.Database.ReadAsync(s => s.Scalar<long>($"SELECT COUNT(*) FROM {table}"), TestContext.Current.CancellationToken);

    Task<string?> RawMetadata(long id) =>
        scratch.Database.ReadAsync(s => s.Scalar<string>("SELECT metadata FROM active_storage_blobs WHERE id = @id", ("@id", id)),
            TestContext.Current.CancellationToken);

    public void Dispose() => scratch.Dispose();
}
