using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Tests.Blobs;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Media;

/// <summary>
/// <c>Marcel::MimeType.for(io, name:, declared_type:)</c> against the reference's answers
/// (vectors/storage.json <c>marcel</c>): fixture files under other names, declared types that
/// disagree, parameters and case in declared types, and inline bytes.
/// </summary>
public sealed class MarcelTests
{
    public static TheoryData<MarcelCase> Cases() => StorageVectors.Marcel();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Content_type_equals_the_reference(MarcelCase vector)
    {
        var data = vector.Fixture is { } fixture ? File.ReadAllBytes(StorageFixture.Fixture(fixture)) : Convert.FromHexString(vector.DataHex ?? "");

        Assert.Equal(vector.ContentType, Marcel.For(data, vector.Name, vector.DeclaredType));
        using var stream = new MemoryStream(data);
        Assert.Equal(vector.ContentType, Marcel.For(stream, vector.Name, vector.DeclaredType));
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public void Staging_identifies_every_upload_as_the_reference_did(StoredMessageAttachment vector)
    {
        using var scratch = new ScratchStorage();
        var storage = BlobStorage(scratch);
        using var source = File.OpenRead(StorageFixture.Fixture(vector.Fixture));

        using var staged = storage.Stage(source, new Filename(vector.Fixture), vector.DeclaredType);

        Assert.Equal(vector.Blob.ContentType, staged.Blob.ContentType);
    }

    public static TheoryData<StoredMessageAttachment> Uploads() => StorageVectors.Messages();

    [Fact]
    public void Only_the_first_bytes_are_read()
    {
        // A JPEG header followed by more bytes than any magic offset reaches.
        var data = new byte[1 << 20];
        data[0] = 0xff;
        data[1] = 0xd8;
        data[2] = 0xff;
        using var stream = new MemoryStream(data);

        Assert.Equal("image/jpeg", Marcel.For(stream, null, null));
        Assert.True(stream.Position < 70_000, $"read {stream.Position} bytes");
    }

    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip", true)]
    [InlineData("application/zip", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData("image/jpeg", "image/jpeg", true)]
    [InlineData("font/otf", "font/ttf", true)]
    public void Children_are_found_through_parents(string child, string parent, bool expected) =>
        Assert.Equal(expected, Marcel.IsChild(child, parent));

    static BlobStorage BlobStorage(ScratchStorage scratch) => Storage.Blobs.BlobStorage.Local(scratch.Root, StorageFixture.Keys, Marcel.Identify);
}
