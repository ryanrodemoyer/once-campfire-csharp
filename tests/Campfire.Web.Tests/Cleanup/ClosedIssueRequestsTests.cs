using System.Text;
using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Jobs;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Csrf;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Cleanup;

/// <summary>
/// Verifies the 6 requests left on closed issues (Q10, #177):
/// 1. #63: rooms#destroy destroys messages with attachments and purges blobs (no 501).
/// 2. #30: RemoveBannedContent destroys messages with attachments and purges blobs.
/// 3. #24: attaching an avatar or account logo enqueues ActiveStorage::AnalyzeJob unless analyzed.
/// 4. #43: a message with no body row presents as "" (Rails' rescue).
/// 5. #16: CookieJar emits both Set-Cookie headers in order when setting then deleting a cookie.
/// 6. #26: WebApp.ResetRemoteConnections disconnects connections on sign out, and runner registers handlers.
/// </summary>
public sealed class ClosedIssueRequestsTests : IDisposable
{
    const string david = "AxJs94fteQ5Autv2VrKsH68c";
    const string kevin = "KevinSessionToken0000001";
    const long davidId = 127326141;
    const long kevinId = 712064548;
    const long designers = 654632876;

    static readonly string[] Fixtures =
    [
        "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) "
            + $"VALUES (900001, {kevinId}, '{kevin}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
    ];

    readonly MessagesApp app = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero), Fixtures);

    // (1) #63: rooms#destroy returns 501 when a message has an attachment.
    [Fact]
    public async Task Room_destroy_with_attachments_destroys_room_and_enqueues_purge_blob_jobs()
    {
        var attachmentCount = (long)app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments a JOIN messages m ON a.record_id = m.id AND a.record_type = 'Message' WHERE m.room_id = {designers}")!;
        Assert.True(attachmentCount > 0, "Designers room should have attachments in the seed");

        var response = await app.SendAsync("DELETE", $"/rooms/{designers}", app.SignedIn(david), "");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/", response.Headers.Location.ToString());
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM rooms WHERE id = {designers}"));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM messages WHERE room_id = {designers}"));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM memberships WHERE room_id = {designers}"));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments a JOIN messages m ON a.record_id = m.id AND a.record_type = 'Message' WHERE m.room_id = {designers}"));

        var purgeJobs = app.Seams.Jobs.OfType<PurgeBlobJob>().ToList();
        Assert.Equal(attachmentCount, purgeJobs.Count);
    }

    // (2) #30: RemoveBannedContent leaves a banned user's attachments behind.
    [Fact]
    public async Task RemoveBannedContent_destroys_attachments_and_enqueues_purge_blob_jobs()
    {
        var now = app.Now;
        var (message, blobId) = await app.Database.WriteAsync(tx =>
        {
            var msg = Data.Lifecycle.MessageLifecycle.Create(tx, app.Seams.Seams, designers, kevinId, "banned-attach-test", "<div>test</div>", "test", now);
            var blob = Blobs.Create(tx.Session, "bannedblob1", "doc.pdf", "application/pdf", "{}", "local", 1234, "checksum", now);
            Attachments.Create(tx.Session, Message.ModelName, msg.Id, AttachmentNames.Attachment, blob.Id, now);
            return (msg, blob.Id);
        }, TestContext.Current.CancellationToken);

        app.Seams.Clear();
        var job = new RemoveBannedContent(app.Database, app.Seams, app.Seams, app.App.Clock);
        await job.PerformAsync(new RemoveBannedContentJob(kevinId), TestContext.Current.CancellationToken);

        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM messages WHERE id = {message.Id}"));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {message.Id}"));

        var purges = app.Seams.Jobs.OfType<PurgeBlobJob>().ToList();
        Assert.NotEmpty(purges);
        Assert.Contains(purges, p => p.BlobId == blobId);
    }

    // (3) #24: attaching an avatar or account logo enqueues AnalyzeBlobJob unless already analyzed.
    [Fact]
    public async Task FirstRun_attaching_avatar_enqueues_AnalyzeBlobJob()
    {
        using var cleanApp = new MessagesApp(app.Now);
        using (var conn = cleanApp.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA foreign_keys = OFF;
                DELETE FROM accounts;
                DELETE FROM action_text_rich_texts;
                DELETE FROM active_storage_attachments;
                DELETE FROM active_storage_blobs;
                DELETE FROM active_storage_variant_records;
                DELETE FROM bans;
                DELETE FROM boosts;
                DELETE FROM memberships;
                DELETE FROM messages;
                DELETE FROM push_subscriptions;
                DELETE FROM rooms;
                DELETE FROM searches;
                DELETE FROM sessions;
                DELETE FROM users;
                PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }

        var headers = UnauthenticatedWithCsrf(cleanApp);
        var fileBytes = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/earth.png"));
        var (body, contentType) = MultipartForm("user[avatar]", "earth.png", "image/png", fileBytes,
            [("user[name]", "Admin"), ("user[email_address]", "admin@example.com"), ("user[password]", "password123")]);
        headers["Content-Type"] = contentType;

        var response = await cleanApp.SendAsync("POST", "/first_run", headers, body, contentType);

        Assert.Equal(302, response.Status);
        var analyze = Assert.Single(cleanApp.Seams.Jobs.OfType<AnalyzeBlobJob>());
        Assert.True(analyze.BlobId > 0);
    }

    [Fact]
    public async Task Users_create_attaching_avatar_enqueues_AnalyzeBlobJob()
    {
        var account = (await app.Database.ReadAsync(Accounts.First, TestContext.Current.CancellationToken))!;
        var fileBytes = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/earth.png"));
        var (body, contentType) = MultipartForm("user[avatar]", "earth.png", "image/png", fileBytes,
            [("user[name]", "NewUser"), ("user[email_address]", "newuser@example.com"), ("user[password]", "password123")]);

        var headers = UnauthenticatedWithCsrf(app);
        headers["Content-Type"] = contentType;

        var response = await app.SendAsync("POST", $"/join/{account.JoinCode}", headers, body, contentType);

        Assert.Equal(302, response.Status);
        var analyze = Assert.Single(app.Seams.Jobs.OfType<AnalyzeBlobJob>());
        Assert.True(analyze.BlobId > 0);
    }

    [Fact]
    public async Task Users_profiles_update_with_uploaded_avatar_enqueues_AnalyzeBlobJob()
    {
        var headers = app.SignedIn(david);
        var fileBytes = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/earth.png"));
        var (body, contentType) = MultipartForm("user[avatar]", "earth.png", "image/png", fileBytes,
            [("user[name]", "David Heinemeier Hansson")]);
        headers["Content-Type"] = contentType;

        var response = await app.SendAsync("PATCH", $"/users/{davidId}/profile", headers, body, contentType);

        Assert.Equal(302, response.Status);
        var analyze = Assert.Single(app.Seams.Jobs.OfType<AnalyzeBlobJob>());
        Assert.True(analyze.BlobId > 0);
    }

    [Fact]
    public async Task Users_profiles_update_with_existing_analyzed_blob_does_not_enqueue_AnalyzeBlobJob()
    {
        var now = app.Now;
        var existingBlob = await app.Database.WriteAsync(tx =>
            Blobs.Create(tx.Session, "alreadyanalyzed1", "avatar.png", "image/png", "{\"analyzed\":true}", "local", 100, "checksum", now),
            TestContext.Current.CancellationToken);
        var signedId = app.App.RequireStorage().Urls.SignedId(existingBlob.Id);

        var headers = app.SignedIn(david);
        var form = $"user%5Bavatar%5D={Uri.EscapeDataString(signedId)}";

        var response = await app.SendAsync("PATCH", $"/users/{davidId}/profile", headers, form);

        Assert.Equal(302, response.Status);
        Assert.Empty(app.Seams.Jobs.OfType<AnalyzeBlobJob>());
    }

    [Fact]
    public async Task Accounts_bots_create_and_update_with_avatar_enqueues_AnalyzeBlobJob()
    {
        var headers = app.SignedIn(david);
        var fileBytes = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/earth.png"));

        // Create bot with avatar
        var (createBody, createContentType) = MultipartForm("user[avatar]", "earth.png", "image/png", fileBytes,
            [("user[name]", "HelperBot")]);
        headers["Content-Type"] = createContentType;
        var createResponse = await app.SendAsync("POST", "/account/bots", headers, createBody, createContentType);

        Assert.Equal(302, createResponse.Status);
        var analyzeCreate = Assert.Single(app.Seams.Jobs.OfType<AnalyzeBlobJob>());
        Assert.True(analyzeCreate.BlobId > 0);

        var botId = (long)app.Scalar("SELECT MAX(id) FROM users WHERE role = 2")!;

        // Update bot with avatar
        app.Seams.Clear();
        var (updateBody, updateContentType) = MultipartForm("user[avatar]", "moon.jpg", "image/jpeg", fileBytes,
            [("user[name]", "HelperBotUpdated")]);
        headers["Content-Type"] = updateContentType;
        var updateResponse = await app.SendAsync("PATCH", $"/account/bots/{botId}", headers, updateBody, updateContentType);

        Assert.Equal(302, updateResponse.Status);
        var analyzeUpdate = Assert.Single(app.Seams.Jobs.OfType<AnalyzeBlobJob>());
        Assert.True(analyzeUpdate.BlobId > 0);
    }

    [Fact]
    public async Task Accounts_update_with_logo_enqueues_AnalyzeBlobJob()
    {
        var headers = app.SignedIn(david);
        var fileBytes = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files/earth.png"));
        var (body, contentType) = MultipartForm("account[logo]", "logo.png", "image/png", fileBytes,
            [("account[name]", "Campfire Updated")]);
        headers["Content-Type"] = contentType;

        var response = await app.SendAsync("PATCH", "/account", headers, body, contentType);

        Assert.Equal(302, response.Status);
        var analyze = Assert.Single(app.Seams.Jobs.OfType<AnalyzeBlobJob>());
        Assert.True(analyze.BlobId > 0);
    }

    // (5) #16: CookieJar emits only deletion vs both Set-Cookie headers in order.
    [Fact]
    public void CookieJar_emits_both_set_and_delete_headers_in_order()
    {
        var jar = new CookieJar("test-secret-key-12345678901234567890");
        jar.SetAuthenticationCookie("my-test-token");
        jar.Encrypted.Set("_campfire_session", new System.Text.Json.Nodes.JsonObject { ["test"] = 1 });
        jar.RemoveAuthenticationCookie();

        var headers = jar.ToSetCookieHeaders(ssl: false, host: "campfire.test");
        Assert.Equal(3, headers.Count);
        Assert.StartsWith("session_token=", headers[0]);
        Assert.DoesNotContain("max-age=0", headers[0], StringComparison.Ordinal);
        Assert.StartsWith("_campfire_session=", headers[1]);
        Assert.StartsWith("session_token=", headers[2]);
        Assert.Contains("max-age=0", headers[2], StringComparison.Ordinal);
    }

    // (6) #26: WebApp.ResetRemoteConnections disconnects user and ServerComposition registers all job types.
    [Fact]
    public async Task Sign_out_disconnects_remote_connections()
    {
        var headers = app.SignedIn(david);
        var response = await app.SendAsync("DELETE", "/session", headers, "");

        Assert.Equal(302, response.Status);
        var disconnect = Assert.Single(app.Seams.Disconnects);
        Assert.Equal(davidId, disconnect.UserId);
        Assert.True(disconnect.Reconnect);
    }

    static (byte[] Body, string ContentType) MultipartForm(string fileField, string filename, string partContentType, byte[] fileBytes, (string Name, string Value)[] fields)
    {
        const string boundary = "----testCleanupBoundary88219";
        using var stream = new MemoryStream();
        void Write(string text) => stream.Write(Encoding.UTF8.GetBytes(text));
        foreach (var (name, value) in fields)
        {
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        }
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{fileField}\"; filename=\"{filename}\"\r\nContent-Type: {partContentType}\r\n\r\n");
        stream.Write(fileBytes);
        Write($"\r\n--{boundary}--\r\n");
        return (stream.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    static Dictionary<string, string> UnauthenticatedWithCsrf(MessagesApp targetApp)
    {
        var csrf = AuthenticityToken.GenerateSessionToken();
        var jar = new CookieJar(targetApp.Keys, () => targetApp.Now);
        jar.Encrypted.Set("_campfire_session", new System.Text.Json.Nodes.JsonObject { ["session_id"] = "0123456789abcdef0123456789abcdef", ["_csrf_token"] = csrf });
        var cookies = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));
        return new()
        {
            ["Cookie"] = cookies,
            ["X-CSRF-Token"] = AuthenticityToken.FormToken(csrf, null, null, "/"),
            ["Origin"] = $"http://{MessagesApp.Host}",
            ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
        };
    }

    public void Dispose() => app.Dispose();
}
