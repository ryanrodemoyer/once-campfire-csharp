using System.IO.Compression;
using System.Text;
using Campfire.Web.Pipeline;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary><c>reference/test/controllers/cached_responses_test.rb</c>, with CSRF protection on.</summary>
public sealed class CachedResponsesTests : IDisposable
{
    readonly MessagesApp app = CacheHarness.Open();

    [Fact]
    public async Task All_four_read_actions_reuse_completed_pages()
    {
        await CacheHarness.WarmAsync(app);
        string[] urls = [$"/rooms/{CacheHarness.AllTalk}", $"/rooms/{CacheHarness.AllTalk}/messages", "/users/me/sidebar", "/searches?q=hello"];
        foreach (var url in urls)
        {
            Assert.Equal(200, (await CacheHarness.GetAsync(app, url)).Status);
        }
        var writes = app.App.Responses.Writes;
        foreach (var url in urls)
        {
            Assert.Equal(200, (await CacheHarness.GetAsync(app, url)).Status);
        }
        Assert.Equal(writes, app.App.Responses.Writes);
    }

    [Fact]
    public async Task Pagination_caches_the_completed_HTML()
    {
        await CacheHarness.WarmAsync(app);
        var first = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.Equal(200, first.Status);
        Assert.StartsWith("text/html", CacheHarness.Header(first.Headers, "Content-Type"), StringComparison.Ordinal);
        var sql = await CacheHarness.QueriesAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.DoesNotContain(sql, statement => statement.Contains("\"messages\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clients_without_token_state_reuse_HTML_and_post_without_tokens()
    {
        await CacheHarness.WarmAsync(app);
        var headers = CacheHarness.DavidHeaders(app);
        var first = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", headers);
        Assert.DoesNotContain("csrf-token", first.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("authenticity_token", first.Body, StringComparison.Ordinal);
        var writes = app.App.Responses.Writes;
        var second = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", headers);
        Assert.Equal(200, second.Status);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(writes, app.App.Responses.Writes);

        headers.Remove("X-CSRF-Token");
        headers["Sec-Fetch-Site"] = "same-origin";
        headers["Accept"] = "text/vnd.turbo-stream.html";
        var posted = await app.SendAsync(
            "POST",
            $"/rooms/{CacheHarness.AllTalk}/messages.turbo_stream",
            headers,
            "message%5Bbody%5D=tokenless+replay+works&message%5Bclient_message_id%5D=cache-replay");
        Assert.Equal(200, posted.Status);
    }

    [Fact]
    public async Task Literal_token_like_text_survives_complete_page_reuse()
    {
        await CacheHarness.WarmAsync(app);
        const string literal = "campfire-csrf-literal authenticity_token csrf-token";
        await Upstream.U05.Seed.PostAsync(app, CacheHarness.AllTalk, $"literal {literal}", "cache-literal");
        var first = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        var second = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Equal(first.Body, second.Body);
        Assert.Contains($"literal {literal}", second.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gzip_bytes_are_reused_and_identity_negotiation_stays_separate()
    {
        await CacheHarness.WarmAsync(app);
        var gzipHeaders = CacheHarness.DavidHeaders(app);
        gzipHeaders["Accept-Encoding"] = "gzip";
        var gzip = await app.SendRawAsync("GET", $"/rooms/{CacheHarness.AllTalk}", gzipHeaders);
        Assert.Equal("gzip", CacheHarness.Header(gzip.Headers, "Content-Encoding"));
        var decoded = Gunzip(gzip.Body);
        Assert.Contains("<html", decoded, StringComparison.Ordinal);
        Assert.Contains("Accept-Encoding", CacheHarness.Header(gzip.Headers, "Vary"), StringComparison.Ordinal);

        var identity = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Equal(decoded, identity.Body);
        var writes = app.App.Responses.Writes;
        var again = await app.SendRawAsync("GET", $"/rooms/{CacheHarness.AllTalk}", gzipHeaders);
        Assert.Equal(gzip.Body, again.Body);
        var plain = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Null(CacheHarness.Header(plain.Headers, "Content-Encoding"));
        Assert.Equal(decoded, plain.Body);

        var identityPreferred = CacheHarness.DavidHeaders(app);
        identityPreferred["Accept-Encoding"] = "gzip;q=0, identity;q=1";
        var preferred = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", identityPreferred);
        Assert.Null(CacheHarness.Header(preferred.Headers, "Content-Encoding"));
        Assert.Equal(decoded, preferred.Body);

        var rejected = CacheHarness.DavidHeaders(app);
        rejected["Accept-Encoding"] = "*;q=0";
        var yielded = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", rejected);
        Assert.Null(CacheHarness.Header(yielded.Headers, "Content-Encoding"));
        Assert.Equal(writes, app.App.Responses.Writes);
    }

    [Fact]
    public async Task Local_and_foreign_commits_invalidate_pages_and_fragments()
    {
        await CacheHarness.WarmAsync(app);
        var message = CacheHarness.Scalar(app, $"SELECT id FROM messages WHERE room_id = {CacheHarness.AllTalk} ORDER BY created_at DESC, id DESC LIMIT 1");
        var creator = CacheHarness.Scalar(app, $"SELECT creator_id FROM messages WHERE id = {message}");
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        await Upstream.U05.Seed.PostAsync(app, CacheHarness.AllTalk, "local commit", "cache-local");
        var local = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Contains("local commit", local.Body, StringComparison.Ordinal);

        CacheHarness.Execute(app, $"UPDATE users SET name = 'Foreign creator' WHERE id = {creator}");
        var renamed = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Contains("Foreign creator", renamed.Body, StringComparison.Ordinal);
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'foreign message body' WHERE record_type = 'Message' AND record_id = {message}");
        var rewritten = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Contains("foreign message body", rewritten.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cached_room_access_and_sessions_are_checked_afresh()
    {
        await CacheHarness.WarmAsync(app);
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        CacheHarness.Execute(app, $"DELETE FROM memberships WHERE room_id = {CacheHarness.AllTalk} AND user_id = {CacheHarness.David}");
        var room = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Equal(302, room.Status);
        Assert.Equal("http://campfire.test/", CacheHarness.Header(room.Headers, "Location"));

        await CacheHarness.GetAsync(app, "/users/me/sidebar");
        CacheHarness.Execute(app, $"DELETE FROM sessions WHERE user_id = {CacheHarness.David}");
        var sidebar = await CacheHarness.GetAsync(app, "/users/me/sidebar");
        Assert.Equal(302, sidebar.Status);
        Assert.Equal("http://campfire.test/session/new", CacheHarness.Header(sidebar.Headers, "Location"));
    }

    [Fact]
    public async Task Conditional_requests_keep_native_validators()
    {
        await CacheHarness.WarmAsync(app);
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        var etag = CacheHarness.Header(page.Headers, "ETag");
        Assert.False(string.IsNullOrEmpty(etag));
        var headers = CacheHarness.DavidHeaders(app);
        headers["If-None-Match"] = etag!;
        var fresh = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages", headers);
        Assert.Equal(304, fresh.Status);
    }

    [Fact]
    public async Task Foreign_presentation_changes_cannot_return_a_false_not_modified()
    {
        await CacheHarness.WarmAsync(app);
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        var etag = CacheHarness.Header(page.Headers, "ETag")!;
        var message = CacheHarness.Scalar(app, $"SELECT id FROM messages WHERE room_id = {CacheHarness.AllTalk} ORDER BY created_at DESC, id DESC LIMIT 1");
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'changed presentation' WHERE record_type = 'Message' AND record_id = {message}");
        var headers = CacheHarness.DavidHeaders(app);
        headers["If-None-Match"] = etag;
        var stale = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages", headers);
        Assert.Equal(200, stale.Status);
        Assert.Contains("changed presentation", stale.Body, StringComparison.Ordinal);
        Assert.NotEqual(etag, CacheHarness.Header(stale.Headers, "ETag"));
    }

    [Fact]
    public async Task Origins_and_frame_variants_have_separate_entries()
    {
        await CacheHarness.WarmAsync(app);
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        var writes = app.App.Responses.Writes;
        var frame = CacheHarness.DavidHeaders(app);
        frame["Turbo-Frame"] = "different-frame";
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", frame);
        var otherHost = CacheHarness.DavidHeaders(app);
        otherHost["Host"] = "campfire.test:8081";
        var response = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", otherHost);
        Assert.Equal(200, response.Status);
        Assert.Equal(writes + 2, app.App.Responses.Writes);
    }

    [Fact]
    public async Task Cache_can_be_disabled()
    {
        app.App.Responses.BudgetOverride = 0;
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        var reads = app.App.Responses.Reads;
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        var again = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Equal(200, again.Status);
        Assert.Equal(reads, app.App.Responses.Reads);
    }

    [Fact]
    public async Task A_commit_after_authentication_cannot_admit_captured_user_fields()
    {
        await CacheHarness.WarmAsync(app);
        app.App.OnSetVersionHeaders = _ =>
            CacheHarness.Execute(app, $"UPDATE users SET name = 'During authentication' WHERE id = {CacheHarness.David}");
        await CacheHarness.GetAsync(app, "/users/me/sidebar");
        app.App.OnSetVersionHeaders = null;
        var writes = app.App.Responses.Writes;
        var page = await CacheHarness.GetAsync(app, "/users/me/sidebar");
        Assert.Equal(writes + 1, app.App.Responses.Writes);
        Assert.Contains("During authentication", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_is_not_reused_across_users_or_sessions()
    {
        // Both sessions exist before the page is warmed. An insert afterwards commits, moves
        // data_version and empties the cache, so the next miss would happen even if the key
        // ignored the user and the token.
        CacheHarness.Execute(app, """
            INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at)
            VALUES (900002, 149087659, 'JasonSessionToken00000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')
            """);
        CacheHarness.Execute(app, """
            INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at)
            VALUES (900003, 127326141, 'DavidSecondSessionToken01', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')
            """);
        await CacheHarness.WarmAsync(app);
        var david = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Contains("name=\"current-user-name\" content=\"David\"", david.Body, StringComparison.Ordinal);
        var writes = app.App.Responses.Writes;
        var davidAgain = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Equal(200, davidAgain.Status);
        Assert.Equal(writes, app.App.Responses.Writes);

        var jasonName = (string)app.Scalar($"SELECT name FROM users WHERE id = {CacheHarness.Jason}")!;
        var jason = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", app.SignedIn("JasonSessionToken00000001", "text/html, application/xhtml+xml"));
        Assert.Equal(200, jason.Status);
        Assert.Contains($"name=\"current-user-name\" content=\"{jasonName}\"", jason.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"current-user-name\" content=\"David\"", jason.Body, StringComparison.Ordinal);
        Assert.Equal(writes + 1, app.App.Responses.Writes);
        var jasonAgain = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", app.SignedIn("JasonSessionToken00000001", "text/html, application/xhtml+xml"));
        Assert.Equal(writes + 1, app.App.Responses.Writes);
        Assert.Equal(jason.Body, jasonAgain.Body);

        writes = app.App.Responses.Writes;
        var otherSession = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", app.SignedIn("DavidSecondSessionToken01", "text/html, application/xhtml+xml"));
        Assert.Equal(200, otherSession.Status);
        Assert.Equal(writes + 1, app.App.Responses.Writes);
    }

    [Fact]
    public void Key_strings_escape_html_the_way_this_app_encodes_json()
    {
        // load_defaults 8.2 leaves U+2028 and U+2029 raw. <, > and & still expand, which is what
        // moves a long URL or user agent across MAX_KEY_BYTES.
        Assert.Equal("\"a\\u003cb\\u003ec\\u0026d\"", ApplicationController.Json("a<b>c&d"));
        Assert.Equal("\"\u2028\u2029\"", ApplicationController.Json("\u2028\u2029"));
        Assert.Equal("\"\\t\"", ApplicationController.Json("\t"));
        Assert.Equal("null", ApplicationController.Json(null));
        var ampersands = new string('&', 400);
        var encoded = ApplicationController.Json(ampersands);
        Assert.True(Encoding.UTF8.GetByteCount(ampersands) < Campfire.Web.ResponseCache.ResponseCache.MaxKeyBytes);
        Assert.True(Encoding.UTF8.GetByteCount(encoded) > Campfire.Web.ResponseCache.ResponseCache.MaxKeyBytes);
    }

    [Fact]
    public async Task A_user_agent_that_exceeds_the_key_cap_once_encoded_is_not_cached()
    {
        await CacheHarness.WarmAsync(app);
        var headers = CacheHarness.DavidHeaders(app);
        headers["User-Agent"] = new string('&', 400);
        var writes = app.App.Responses.Writes;
        var first = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", headers);
        var second = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}", headers);
        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(writes, app.App.Responses.Writes);
    }

    public void Dispose() => app.Dispose();

    static string Gunzip(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
