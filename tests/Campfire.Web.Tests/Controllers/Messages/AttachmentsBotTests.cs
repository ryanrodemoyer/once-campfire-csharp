using Campfire.Data.Events;
using Campfire.Jobs.Webhooks;
using Campfire.Storage.Media;
using Campfire.Vectors;
using Campfire.Web.Controllers;
using DataQueries = Campfire.Data.Queries;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// The bot API and a webhook attachment reply (reference/test/controllers/messages/by_bots_controller_test.rb
/// "create file", and reference/test/models/webhook_test.rb "delivery with OK attachment reply"):
/// the same <c>create_with_attachment!</c> and <c>purge_later</c> the composer uses, driven from
/// the hooks I05 left. The composer's replay is <see cref="AttachmentsTests"/>.
/// </summary>
public sealed class AttachmentsBotTests : IDisposable
{
    const long headquarters = 201306877;
    const long deployBot = 773523956;

    readonly AttachmentsApp app = new(DateTimeOffset.Parse("2026-03-02T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task A_bot_uploads_a_file_and_destroying_the_message_purges_it_later()
    {
        RequireMediaTools();
        var upload = UploadBody();
        var created = await app.SendAsync(
            "POST", $"/rooms/{headquarters}/773523956-DeployBot456/messages", Bot(), upload.Body, upload.ContentType);
        Assert.Equal(201, created.Status);
        var messageId = (long)app.Scalar("SELECT id FROM messages WHERE creator_id = 773523956 AND room_id = 201306877 ORDER BY id DESC LIMIT 1")!;
        Assert.Equal($"http://campfire.test/messages/{messageId}", created.Headers.Location.ToString());
        var blobId = (long)app.Scalar(
            $"SELECT blob_id FROM active_storage_attachments WHERE record_type = 'Message' AND name = 'attachment' AND record_id = {messageId}")!;
        Assert.Contains("\"analyzed\":true", (string)app.Scalar($"SELECT metadata FROM active_storage_blobs WHERE id = {blobId}")!, StringComparison.Ordinal);
        Assert.Equal(1L, app.Scalar($"SELECT COUNT(*) FROM active_storage_variant_records WHERE blob_id = {blobId}"));
        Assert.Equal(
            ["Room::PushMessageJob", "ActiveStorage::AnalyzeJob", "ActiveStorage::AnalyzeJob"],
            Jobs());

        app.Recording.Clear();
        var destroyed = await app.SendAsync("DELETE", $"/rooms/{headquarters}/773523956-DeployBot456/messages/{messageId}", Bot());
        Assert.Equal(204, destroyed.Status);
        Assert.Equal(["ActiveStorage::PurgeJob"], Jobs());
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM messages WHERE id = {messageId}"));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {messageId}"));
        // The blob stays until PurgeJob runs, as `purge_later` leaves it.
        Assert.Equal(1L, app.Scalar($"SELECT COUNT(*) FROM active_storage_blobs WHERE id = {blobId}"));
    }

    [Fact]
    public async Task A_webhook_attachment_reply_creates_the_message_and_processes_the_file()
    {
        RequireMediaTools();
        var bytes = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/moon.jpg"));
        var room = await app.Database.ReadAsync(session => DataQueries.Rooms.Find(session, headquarters)!, TestContext.Current.CancellationToken);
        var bot = await app.Database.ReadAsync(session => DataQueries.Users.Find(session, deployBot)!, TestContext.Current.CancellationToken);
        var replies = new ByBotsWebhookReplies(app.App);
        await replies.ReceiveAttachmentAsync(room, bot, new WebhookAttachmentReply(bytes, "attachment.jpg", "image/jpeg"), TestContext.Current.CancellationToken);

        var messageId = (long)app.Scalar("SELECT id FROM messages WHERE creator_id = 773523956 AND room_id = 201306877 ORDER BY id DESC LIMIT 1")!;
        var blob = app.Scalar(
            $"SELECT filename || ' ' || content_type || ' ' || metadata FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id WHERE a.record_type = 'Message' AND a.name = 'attachment' AND a.record_id = {messageId}") as string;
        Assert.Equal("""attachment.jpg image/jpeg {"identified":true,"width":640,"height":640,"analyzed":true}""", blob);
        Assert.Equal("attachment.jpg", app.Scalar($"SELECT body FROM message_search_index WHERE rowid = {messageId}"));
        Assert.Equal(
            ["Room::PushMessageJob", "ActiveStorage::AnalyzeJob", "ActiveStorage::AnalyzeJob"],
            Jobs());
        Assert.Contains(app.Recording.Events, seamEvent => seamEvent is Broadcast);
    }

    static void RequireMediaTools()
    {
        if (!LibVips.IsAvailable)
        {
            Assert.Skip("libvips isn't installed");
        }
    }

    List<string> Jobs() => [.. app.Recording.Events.OfType<Enqueued>().Select(enqueued => enqueued.Job.ClassName)];

    static Dictionary<string, string> Bot() => new()
    {
        ["Accept"] = "*/*",
        ["User-Agent"] = "curl/8.5.0",
    };

    static (byte[] Body, string ContentType) UploadBody()
    {
        const string boundary = "----m10BotBoundary7MA4YWxkTrZu0gW";
        var file = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/moon.jpg"));
        using var body = new MemoryStream();
        void Write(string text) => body.Write(System.Text.Encoding.UTF8.GetBytes(text));
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"attachment\"; filename=\"moon.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n");
        body.Write(file);
        Write($"\r\n--{boundary}--\r\n");
        return (body.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    public void Dispose() => app.Dispose();
}
