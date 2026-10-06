using System.Text;
using System.Text.Json.Nodes;
using Campfire.Storage.Blobs;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Blobs;

/// <summary>Signed blob ids and URLs against what the reference generated for its seed blobs.</summary>
public class BlobUrlTests
{
    static readonly BlobStorage Storage = StorageFixture.Storage("/rails/storage/files");
    static readonly DateTimeOffset Now = StorageFixture.Time("2026-01-01T00:00:00Z");

    public static TheoryData<StoredMessageAttachment> Messages() => StorageVectors.Messages();

    [Theory]
    [MemberData(nameof(Messages))]
    public void Blob_paths_equal_the_reference(StoredMessageAttachment vector)
    {
        var blob = StorageFixture.BlobFrom(vector.Blob);

        Assert.Equal(vector.RailsBlobPath, Storage.Urls.BlobRedirectPath(blob));
        Assert.Equal(vector.RailsBlobDownloadPath, Storage.Urls.BlobRedirectPath(blob, disposition: "attachment"));
        Assert.Equal(vector.RailsBlobProxyPath, Storage.Urls.BlobProxyPath(blob));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Service_urls_equal_the_reference(StoredMessageAttachment vector)
    {
        var blob = StorageFixture.BlobFrom(vector.Blob);

        Assert.Equal(vector.ServiceUrl, Storage.Url(blob, StorageFixture.BaseUrl, expiresAt: null));
        Assert.Equal(vector.ServiceUrlAttachment, Storage.Url(blob, StorageFixture.BaseUrl, expiresAt: null, disposition: "attachment"));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Representation_paths_equal_the_reference(StoredMessageAttachment vector)
    {
        var blob = StorageFixture.BlobFrom(vector.Blob);
        foreach (var (redirect, proxy) in new[] { (vector.ThumbPath, vector.ThumbProxyPath), (vector.PosterPath, vector.PosterProxyPath) })
        {
            if (redirect is null || proxy is null)
            {
                continue;
            }
            // The variation key is S02's; the signed blob id, filename and layout are checked here.
            var variationKey = Uri.UnescapeDataString(redirect.Split('/')[^2]);

            Assert.Equal(redirect, Storage.Urls.RepresentationRedirectPath(blob, variationKey));
            Assert.Equal(proxy, Storage.Urls.RepresentationProxyPath(blob, variationKey));
        }
    }

    [Fact]
    public void Avatar_representation_path_equals_the_reference()
    {
        var avatar = StorageVectors.File.Avatars[0];
        var variationKey = Uri.UnescapeDataString(avatar.Path!.Split('/')[^2]);

        Assert.Equal(avatar.Path, Storage.Urls.RepresentationRedirectPath(StorageFixture.BlobFrom(avatar.Blob), variationKey));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Reference_signed_ids_and_disk_urls_resolve(StoredMessageAttachment vector)
    {
        var signedId = Uri.UnescapeDataString(vector.RailsBlobPath.Split('/')[^2]);
        Assert.Equal(vector.Blob.Id, Storage.Urls.VerifySignedId(signedId, Now));

        var encodedKey = Uri.UnescapeDataString(new Uri(vector.ServiceUrlAttachment).AbsolutePath.Split('/')[^2]);
        var key = Storage.Service.DecodeVerifiedKey(encodedKey, Now);
        Assert.NotNull(key);
        Assert.Equal(vector.Blob.Key, key.Key);
        Assert.Equal(vector.Blob.ContentType, key.ContentType);
        Assert.Equal(DiskService.LocalName, key.ServiceName);
        Assert.StartsWith("attachment; filename=", key.Disposition, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_ids_are_bound_to_their_purpose()
    {
        var variation = Storage.Verifier.Generate(JsonValue.Create(1), "variation");

        Assert.Null(Storage.Urls.VerifySignedId(variation, Now));
        Assert.Null(Storage.Urls.VerifySignedId(Storage.Urls.SignedId(1) + "x", Now));
        Assert.Null(Storage.Service.DecodeVerifiedKey(Storage.Urls.SignedId(1), Now));
        Assert.Equal(7, Storage.Urls.VerifySignedId(Storage.Urls.SignedId(7), Now));
    }

    [Fact]
    public void Expiring_messages_equal_the_reference()
    {
        var expiresAt = StorageFixture.Time("2030-01-02T03:04:05.678Z");

        Assert.Equal(StorageVectors.File.Verifier.Expiring, Storage.Verifier.Generate(JsonValue.Create("x"), "p", expiresAt));
        Assert.Equal("x", Storage.Verifier.Verify(StorageVectors.File.Verifier.Expiring, "p", Now).Value!.GetValue<string>());
        Assert.False(Storage.Verifier.Verify(StorageVectors.File.Verifier.Expiring, "p", expiresAt.AddSeconds(1)).IsValid);
    }

    [Fact]
    public void Disk_url_paths_equal_the_reference()
    {
        var weird = new Filename("weird & <name> ünï.png");
        const string key = "abcdefghijklmnopqrstuvwxyz12";

        Assert.Equal(StorageVectors.File.Verifier.DiskUrlPath, Storage.Service.UrlPath(key, null, weird, "image/png", "inline"));
        Assert.Equal(StorageVectors.File.Verifier.DiskUrlPathNilType, Storage.Service.UrlPath(key, null, weird, null, "attachment"));
    }

    [Fact]
    public void Direct_upload_path_equals_the_reference()
    {
        var expected = StorageVectors.File.Verifier.DirectUploadPath;
        var token = Uri.UnescapeDataString(expected.Split('/')[^1]);
        // The reference signed it a minute after it ran; read that expiry back out of the payload.
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(token[..token.IndexOf("--", StringComparison.Ordinal)])))!;
        var expiresAt = StorageFixture.Time(payload["_rails"]!["exp"]!.GetValue<string>());

        Assert.Equal(expected, Storage.Service.DirectUploadPath("abcdefghijklmnopqrstuvwxyz12", expiresAt, "image/png", 42, "abc=="));

        var decoded = Storage.Service.DecodeVerifiedToken(token, expiresAt.AddSeconds(-1));
        Assert.Equal(new DiskToken("abcdefghijklmnopqrstuvwxyz12", "image/png", 42, "abc==", "local"), decoded);
        Assert.Null(Storage.Service.DecodeVerifiedToken(token, expiresAt.AddSeconds(1)));
    }

    public static TheoryData<StoredBlob> AllBlobs() => VectorFiles.Rows(
        StorageVectors.File.Messages.Select(m => m.Blob)
            .Concat(StorageVectors.File.Messages.SelectMany(m => m.Variants ?? []).Select(v => v.Blob))
            .Concat(StorageVectors.File.Avatars.Concat(StorageVectors.File.Logos).SelectMany(i => i.Variants.Select(v => v.Blob).Prepend(i.Blob))),
        b => $"{b.Id} {b.Filename}");

    [Theory]
    [MemberData(nameof(AllBlobs))]
    public void Files_live_where_the_disk_service_puts_them(StoredBlob vector)
    {
        Assert.Equal(Path.Combine("/rails/storage/files", vector.Path), Storage.Service.PathFor(vector.Key));
        Assert.Equal(BlobKey.Length, vector.Key.Length);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Variable_matches_the_reference(StoredMessageAttachment vector) =>
        Assert.Equal(vector.Variable, StorageFixture.BlobFrom(vector.Blob).IsVariable);

    [Theory]
    [InlineData("text/html", "application/octet-stream", "attachment")]
    [InlineData("image/svg+xml", "application/octet-stream", "attachment")]
    [InlineData("image/png", "image/png", null)]
    [InlineData("application/pdf", "application/pdf", null)]
    [InlineData("video/quicktime", "video/quicktime", "attachment")]
    [InlineData("text/plain", "text/plain", "attachment")]
    public void Unsafe_types_are_served_as_binary_attachments(string contentType, string served, string? forced)
    {
        var blob = new Blob(1, "k", new Filename("f"), contentType, [], "local", 0, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(served, blob.ContentTypeForServing);
        Assert.Equal(forced, blob.ForcedDispositionForServing);
    }
}
