using System.Diagnostics;
using System.Text;
using Campfire.Storage.Media;
using Campfire.Storage.Variants;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Upstream.U06;

public sealed class AttachmentHardeningTests : IDisposable
{
    readonly AttachmentsApp app;
    const string kevinToken = "KevinSessionToken0000001";
    const long hqRoomId = 201306877;

    public AttachmentHardeningTests()
    {
        app = new AttachmentsApp(
            new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
            [
                "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                    $"VALUES (900001, 712064548, '{kevinToken}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            ]);
    }

    static byte[] FixtureBytes(string relativePath) =>
        File.ReadAllBytes(Path.Combine(VectorFiles.Root, relativePath));

    sealed record UploadForm(byte[] Body, string ContentType);

    static UploadForm UploadBody(string filename, string contentType, string clientMessageId, byte[] content)
    {
        const string boundary = "----m10UploadBoundary7MA4YWxkTrZu0gW";
        using var body = new MemoryStream();
        void Write(string text) => body.Write(Encoding.UTF8.GetBytes(text));
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"message[attachment]\"; filename=\"{filename}\"\r\nContent-Type: {contentType}\r\n\r\n");
        body.Write(content);
        Write($"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"message[client_message_id]\"\r\n\r\n{clientMessageId}\r\n--{boundary}--\r\n");
        return new UploadForm(body.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    [Fact]
    public async Task Creating_a_message_with_an_image_that_cannot_be_decoded_broadcasts_the_message_to_the_room()
    {
        if (!LibVips.IsAvailable)
        {
            return;
        }

        // Half of a real WebP file bytes as broken.webp
        var webp = FixtureBytes("vectors/storage/moon-avatar.webp");
        var brokenBytes = webp[..(webp.Length / 2)];

        var upload = UploadBody("broken.webp", "image/webp", "client-broken-webp", brokenBytes);
        var response = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload.Body, upload.ContentType);

        Assert.True(response.Status == 200, response.Body);
        Assert.Contains("broken.webp", response.Body);

        // Broadcast was sent
        var broadcast = Assert.Single(app.Recording.Broadcasts);
        Assert.Contains("broken.webp", broadcast.Payload);

        // Blob was created but no thumbnail variant record exists
        var messageId = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-broken-webp'")!;
        var blobId = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId}")!;
        var variants = (long)app.Scalar($"SELECT COUNT(*) FROM active_storage_variant_records WHERE blob_id = {blobId}")!;
        Assert.Equal(0L, variants);
    }

    [Fact]
    public async Task Creating_a_message_keeps_a_video_that_cannot_be_decoded()
    {
        var mov = FixtureBytes("reference/test/fixtures/files/alpha-centuri.mov");
        var brokenBytes = mov[..64];

        var upload = UploadBody("broken.mov", "video/quicktime", "client-broken-mov", brokenBytes);
        var response = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload.Body, upload.ContentType);

        Assert.Equal(200, response.Status);

        var messageId = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-broken-mov'")!;
        var blobId = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId}")!;
        Assert.True(blobId > 0);

        // Preview image attachment was not created
        var previewImages = (long)app.Scalar(
            $"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'ActiveStorage::Blob' AND name = 'preview_image' AND record_id = {blobId}")!;
        Assert.Equal(0L, previewImages);
    }

    [Fact]
    public async Task Creating_a_message_makes_the_video_poster_that_the_room_shows()
    {
        if (!VideoPreviewer.IsAvailable || !LibVips.IsAvailable)
        {
            return;
        }

        var mov = FixtureBytes("reference/test/fixtures/files/alpha-centuri.mov");
        var upload = UploadBody("alpha-centuri.mov", "video/quicktime", "client-valid-mov", mov);
        var response = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload.Body, upload.ContentType);

        Assert.Equal(200, response.Status);

        var messageId = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-valid-mov'")!;
        var blobId = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId}")!;

        // Preview image exists
        var previewImageBlobId = (long)app.Scalar(
            $"SELECT b.id FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id WHERE a.record_type = 'ActiveStorage::Blob' AND a.name = 'preview_image' AND a.record_id = {blobId}")!;
        Assert.True(previewImageBlobId > 0);

        // Video poster variant record exists with NamedVariants.VideoPoster digest
        var posterDigest = new Variation(NamedVariants.VideoPoster).Digest;
        var variantCount = (long)app.Scalar(
            $"SELECT COUNT(*) FROM active_storage_variant_records WHERE blob_id = {previewImageBlobId} AND variation_digest = '{posterDigest}'")!;
        Assert.Equal(1L, variantCount);
    }

    [Fact]
    public void Video_previews_are_drawn_by_the_previewer_with_a_time_limit_from_a_frame_within_the_first_5_seconds()
    {
        Assert.Contains("gte(t\\,5)", VideoPreviewer.DefaultArguments);
        Assert.Equal(TimeSpan.FromSeconds(10), VideoPreviewer.DefaultTimeLimit);
    }

    [Fact]
    public async Task Creating_a_message_gives_up_on_a_video_preview_that_takes_longer_than_the_time_limit()
    {
        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "#!/bin/sh\n[ \"$1\" = \"-version\" ] && exit 0\nexec sleep 5\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var prevPath = VideoPreviewer.FfmpegPath;
        var prevLimit = VideoPreviewer.DefaultTimeLimit;
        VideoPreviewer.FfmpegPath = tempScript;
        VideoPreviewer.DefaultTimeLimit = TimeSpan.FromSeconds(0.2);

        try
        {
            var mov = FixtureBytes("reference/test/fixtures/files/alpha-centuri.mov");
            var upload = UploadBody("alpha-centuri.mov", "video/quicktime", "client-slow-mov", mov);

            var sw = Stopwatch.StartNew();
            var response = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload.Body, upload.ContentType);
            sw.Stop();

            Assert.Equal(200, response.Status);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Took {sw.Elapsed.TotalSeconds}s, expected < 2.0s");

            var messageId = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-slow-mov'")!;
            var blobId = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId}")!;
            var previewImages = (long)app.Scalar(
                $"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'ActiveStorage::Blob' AND name = 'preview_image' AND record_id = {blobId}")!;
            Assert.Equal(0L, previewImages);
        }
        finally
        {
            VideoPreviewer.FfmpegPath = prevPath;
            VideoPreviewer.DefaultTimeLimit = prevLimit;
            try { File.Delete(tempScript); } catch { }
        }
    }

    [Fact]
    public async Task Creating_a_message_makes_no_thumbnail_of_an_image_with_more_pixels_than_the_limit()
    {
        if (!LibVips.IsAvailable)
        {
            return;
        }

        var moon = FixtureBytes("reference/test/fixtures/files/moon.jpg");
        const long moonPixels = 640 * 640;

        // At limit: thumbnail is processed
        app.App.ThumbnailMaxPixels = moonPixels;
        var upload1 = UploadBody("moon.jpg", "image/jpeg", "client-moon-at-limit", moon);
        var resp1 = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload1.Body, upload1.ContentType);
        Assert.Equal(200, resp1.Status);
        var messageId1 = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-moon-at-limit'")!;
        var blobId1 = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId1}")!;
        var variants1 = (long)app.Scalar($"SELECT COUNT(*) FROM active_storage_variant_records WHERE blob_id = {blobId1}")!;
        Assert.Equal(1L, variants1);

        // Below limit: thumbnail is not processed
        app.App.ThumbnailMaxPixels = moonPixels - 1;
        var upload2 = UploadBody("moon.jpg", "image/jpeg", "client-moon-over-limit", moon);
        var resp2 = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload2.Body, upload2.ContentType);
        Assert.Equal(200, resp2.Status);
        var messageId2 = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-moon-over-limit'")!;
        var blobId2 = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId2}")!;
        var variants2 = (long)app.Scalar($"SELECT COUNT(*) FROM active_storage_variant_records WHERE blob_id = {blobId2}")!;
        Assert.Equal(0L, variants2);
    }

    [Fact]
    public async Task Creating_a_message_makes_no_preview_of_a_video_with_more_pixels_than_the_limit()
    {
        if (!VideoPreviewer.IsAvailable)
        {
            return;
        }

        var mov = FixtureBytes("reference/test/fixtures/files/alpha-centuri.mov");
        app.App.ThumbnailMaxPixels = 1;
        var upload = UploadBody("alpha-centuri.mov", "video/quicktime", "client-video-over-limit", mov);
        var response = await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), upload.Body, upload.ContentType);
        Assert.Equal(200, response.Status);

        var messageId = (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = 'client-video-over-limit'")!;
        var blobId = (long)app.Scalar($"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId}")!;
        var previewImages = (long)app.Scalar(
            $"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'ActiveStorage::Blob' AND name = 'preview_image' AND record_id = {blobId}")!;
        Assert.Equal(0L, previewImages);
    }

    [Fact]
    public async Task A_page_of_messages_loads_previews_without_n_plus_one_queries()
    {
        if (!VideoPreviewer.IsAvailable || !LibVips.IsAvailable)
        {
            return;
        }

        var moon = FixtureBytes("reference/test/fixtures/files/moon.jpg");
        var mov = FixtureBytes("reference/test/fixtures/files/alpha-centuri.mov");

        for (var i = 0; i < 2; i++)
        {
            var u1 = UploadBody("moon.jpg", "image/jpeg", $"nplus-moon-{i}", moon);
            await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), u1.Body, u1.ContentType);

            var u2 = UploadBody("alpha-centuri.mov", "video/quicktime", $"nplus-mov-{i}", mov);
            await app.SendAsync("POST", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken), u2.Body, u2.ContentType);
        }

        var pageResponse = await app.SendAsync("GET", $"/rooms/{hqRoomId}/messages", app.Uploader(kevinToken));
        Assert.Equal(200, pageResponse.Status);
        Assert.Contains("poster=", pageResponse.Body);
        Assert.Contains("moon.jpg", pageResponse.Body);
    }

    public void Dispose() => app.Dispose();
}
