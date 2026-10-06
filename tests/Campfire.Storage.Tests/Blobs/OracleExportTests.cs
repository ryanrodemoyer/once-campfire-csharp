using System.Text.Json;
using Campfire.RailsCompat.Params;
using Campfire.Storage.Blobs;

namespace Campfire.Storage.Tests.Blobs;

/// <summary>
/// Writes blobs into a database and storage directory for Oracle/read.rb, which boots Active
/// Storage at the reference's Rails revision on them. Runs only from Oracle/run.sh, which sets
/// <c>CAMPFIRE_STORAGE_ORACLE_DIR</c>.
/// </summary>
public class OracleExportTests
{
    static readonly DateTimeOffset Now = StorageFixture.Time("2026-10-05T12:34:56.789012Z");

    [Fact]
    public async Task Writes_blobs_for_the_reference_to_read()
    {
        var directory = Environment.GetEnvironmentVariable("CAMPFIRE_STORAGE_ORACLE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "run tests/Campfire.Storage.Tests/Oracle/run.sh");

        using var scratch = new ScratchStorage(directory);
        var uploads = new (string Fixture, string Filename, string? Type, string RecordType, long RecordId, string Name)[]
        {
            ("moon.jpg", "moon.jpg", "image/jpeg", "Message", 1, "attachment"),
            ("alpha-centuri.mov", "alpha-centuri.mov", "video/quicktime", "Message", 2, "attachment"),
            ("earth.png", "weird & <name> ünï.png", "image/png", "User", 1, "avatar"),
            ("pixel.bmp", " a/b\\c:d.bmp ", "image/bmp", "Account", 1, "logo"),
        };
        var blobs = new List<object>();
        foreach (var (fixture, filename, type, recordType, recordId, name) in uploads)
        {
            using var upload = UploadedFile.FromBytes(filename, type, File.ReadAllBytes(StorageFixture.Fixture(fixture)));
            using var staged = scratch.Storage.Stage(upload);
            var attached = await scratch.Database.WriteAsync(tx =>
                BlobStorage.AttachOne(tx, staged, recordType, recordId, name, Now), TestContext.Current.CancellationToken);
            var blob = attached.Blob;
            blobs.Add(new
            {
                id = blob.Id,
                fixture,
                key = blob.Key,
                filename = blob.Filename.Value,
                content_type = blob.ContentType,
                byte_size = blob.ByteSize,
                checksum = blob.Checksum,
                created_at = Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture),
                path = Path.GetRelativePath(scratch.Root, scratch.Storage.Service.PathFor(blob.Key)),
                attachment = new { id = attached.Attachment.Id, name, record_type = recordType, record_id = recordId },
                signed_id = scratch.Storage.Urls.SignedId(blob.Id),
                rails_blob_path = scratch.Storage.Urls.BlobRedirectPath(blob),
                rails_blob_download_path = scratch.Storage.Urls.BlobRedirectPath(blob, "attachment"),
                rails_blob_proxy_path = scratch.Storage.Urls.BlobProxyPath(blob),
                service_url = scratch.Storage.Url(blob, StorageFixture.BaseUrl, expiresAt: null),
                service_url_attachment = scratch.Storage.Url(blob, StorageFixture.BaseUrl, expiresAt: null, disposition: "attachment"),
            });
        }
        var expiresAt = StorageFixture.Time("2030-01-02T03:04:05.678Z");
        var manifest = new
        {
            blobs,
            direct_upload = new
            {
                expires_at = "2030-01-02T03:04:05.678Z",
                path = scratch.Storage.Service.DirectUploadPath("abcdefghijklmnopqrstuvwxyz12", expiresAt, "image/png", 42, "abc=="),
            },
        };
        await File.WriteAllTextAsync(Path.Combine(directory!, "manifest.json"), JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
    }
}
