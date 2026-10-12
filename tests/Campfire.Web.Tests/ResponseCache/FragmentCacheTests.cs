using System.Text.Json;
using Campfire.Web.Controllers;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary><c>reference/test/controllers/fragment_cache_test.rb</c> (<c>FragmentRenderingTest</c>).</summary>
public sealed class FragmentCacheTests : IDisposable
{
    readonly MessagesApp app = CacheHarness.Open();

    [Fact]
    public async Task Collection_caching_uses_the_fragment_store_and_leaves_rate_limits()
    {
        app.App.Responses.BudgetOverride = 0;
        await CacheHarness.WarmAsync(app);
        var counted = SessionsController.CreateRateLimit.Increment(app.App, "rate-limit:fragments", app.Now);
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        app.App.Fragments.Track = true;
        app.App.Fragments.ClearStats();
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.Equal(200, page.Status);
        Assert.NotEmpty(app.App.Fragments.HitKeys);
        Assert.DoesNotContain(app.App.Fragments.HitKeys, key => key.Contains(CacheHarness.DavidSession, StringComparison.Ordinal));
        app.App.Fragments.Clear();
        Assert.Equal(counted + 1, SessionsController.CreateRateLimit.Increment(app.App, "rate-limit:fragments", app.Now));
    }

    [Fact]
    public async Task Disabled_page_caching_shows_a_foreign_body_write()
    {
        app.App.Responses.BudgetOverride = 0;
        await CacheHarness.WarmAsync(app);
        var message = LatestMessage();
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'foreign disabled body' WHERE record_type = 'Message' AND record_id = {message}");
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.Equal(200, page.Status);
        Assert.Contains("foreign disabled body", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conditional_pagination_invalidates_warmed_fragments()
    {
        await CacheHarness.WarmAsync(app);
        var headers = CacheHarness.DavidHeaders(app);
        headers["If-None-Match"] = "unmatched";
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages", headers);
        Assert.Equal(200, page.Status);
        var etag = CacheHarness.Header(page.Headers, "ETag")!;
        var message = LatestMessage();
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'foreign conditional body' WHERE record_type = 'Message' AND record_id = {message}");
        headers["If-None-Match"] = etag;
        var stale = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages", headers);
        Assert.Equal(200, stale.Status);
        Assert.Contains("foreign conditional body", stale.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_controls_survive_creator_edits_while_the_author_is_fresh()
    {
        await CacheHarness.WarmAsync(app);
        app.App.Fragments.Track = true;
        var message = LatestMessage();
        var creator = CacheHarness.Scalar(app, $"SELECT creator_id FROM messages WHERE id = {message}");
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        var renders = app.App.Fragments.ActionRenders;
        CacheHarness.Execute(app, $"UPDATE users SET name = 'Fresh creator with cached controls' WHERE id = {creator}");
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.Equal(200, page.Status);
        Assert.Equal(renders, app.App.Fragments.ActionRenders);
        Assert.Contains("Fresh creator with cached controls", page.Body, StringComparison.Ordinal);
        Assert.Contains($"action=\"/messages/{message}/boosts\"", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_in_another_room_retains_collection_hits()
    {
        app.App.Responses.BudgetOverride = 0;
        await CacheHarness.WarmAsync(app);
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        CacheHarness.Execute(app, $"UPDATE rooms SET name = 'Unrelated room rename' WHERE id = {CacheHarness.Pets}");
        app.App.Fragments.Track = true;
        app.App.Fragments.ClearStats();
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.Equal(200, page.Status);
        // All Talk has 131 messages and the index renders one page of 40. Rails' watercooler
        // fits on one page, so that test compares cache_hits to room.messages.count. Here every
        // rendered message is a hit, which is that comparison for the page that was drawn.
        var collection = Assert.Single(app.App.Fragments.Collections, item => item.Identifier.EndsWith("messages/_message.html.erb", StringComparison.Ordinal));
        Assert.Equal(collection.Count, collection.Hits);
        Assert.Equal(40, collection.Count);
    }

    [Fact]
    public async Task A_foreign_commit_after_capture_bypasses_old_fragments()
    {
        app.App.Responses.BudgetOverride = 0;
        await CacheHarness.WarmAsync(app);
        var message = LatestMessage();
        await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        app.App.OnSetVersionHeaders = _ =>
            CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'foreign before fragments' WHERE record_type = 'Message' AND record_id = {message}");
        var headers = CacheHarness.DavidHeaders(app);
        headers["If-None-Match"] = "unmatched";
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages", headers);
        Assert.Equal(200, page.Status);
        Assert.Contains("foreign before fragments", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_streams_recheck_body_creator_and_boost()
    {
        await CacheHarness.WarmAsync(app);
        var message = LatestMessage();
        var creator = CacheHarness.Scalar(app, $"SELECT creator_id FROM messages WHERE id = {message}");
        var path = $"/rooms/{CacheHarness.AllTalk}/refresh.turbo_stream?since=0";
        Assert.Equal(200, (await CacheHarness.GetAsync(app, path)).Status);
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'foreign refreshed body' WHERE record_type = 'Message' AND record_id = {message}");
        CacheHarness.Execute(app, $"UPDATE users SET name = 'Foreign refreshed creator' WHERE id = {creator}");
        CacheHarness.Execute(app, $"UPDATE boosts SET content = 'refreshed' WHERE message_id = {message}");
        if (CacheHarness.Scalar(app, $"SELECT COUNT(*) FROM boosts WHERE message_id = {message}") == 0)
        {
            CacheHarness.Execute(app, $"""
                INSERT INTO boosts (booster_id, content, created_at, message_id, updated_at)
                VALUES ({creator}, 'refreshed', '2026-03-02 15:00:00', {message}, '2026-03-02 15:00:00')
                """);
        }
        var page = await CacheHarness.GetAsync(app, path);
        Assert.Equal(200, page.Status);
        Assert.Contains("foreign refreshed body", page.Body, StringComparison.Ordinal);
        Assert.Contains("Foreign refreshed creator", page.Body, StringComparison.Ordinal);
        Assert.Contains("refreshed", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_fragments_use_the_bounded_store_and_create_no_HTML_session()
    {
        await CacheHarness.WarmAsync(app);
        var message = LatestMessage();
        var path = $"/rooms/{CacheHarness.AllTalk}/{CacheHarness.BenderKey}/messages";
        var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
        var first = await app.SendAsync("GET", path, headers);
        Assert.Equal(200, first.Status);
        Assert.DoesNotContain(first.Headers.SetCookie, cookie => cookie!.StartsWith("_campfire_session=", StringComparison.Ordinal));
        app.App.Fragments.Track = true;
        app.App.Fragments.ClearStats();
        var second = await app.SendAsync("GET", path, headers);
        Assert.Equal(200, second.Status);
        Assert.NotEmpty(app.App.Fragments.HitKeys);
        Assert.DoesNotContain(app.App.Fragments.HitKeys, key => key.Contains("BenderBot123", StringComparison.Ordinal));
        var creator = CacheHarness.Scalar(app, $"SELECT creator_id FROM messages WHERE id = {message}");
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'foreign JSON body' WHERE record_type = 'Message' AND record_id = {message}");
        CacheHarness.Execute(app, $"UPDATE users SET name = 'Foreign JSON creator' WHERE id = {creator}");
        var changed = await app.SendAsync("GET", path, headers);
        var row = JsonRow(changed.Body, message);
        Assert.Contains("foreign JSON body", row.GetProperty("body").GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Equal("Foreign JSON creator", row.GetProperty("creator").GetProperty("name").GetString());
        headers["Host"] = "another.campfire.test:8081";
        var other = await app.SendAsync("GET", path, headers);
        Assert.Contains("another.campfire.test:8081", JsonRow(other.Body, message).GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_create_broadcast_does_not_read_the_fragment_store_for_the_new_body()
    {
        await CacheHarness.WarmAsync(app);
        var writes = app.App.Fragments.Writes;
        var posted = await Upstream.U05.Seed.PostAsync(app, CacheHarness.AllTalk, "foreign broadcast body", "cache-broadcast");
        Assert.True(posted > 0);
        Assert.Equal(writes, app.App.Fragments.Writes);
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}");
        Assert.Contains("foreign broadcast body", page.Body, StringComparison.Ordinal);
    }

    public void Dispose() => app.Dispose();

    long LatestMessage() =>
        CacheHarness.Scalar(app, $"SELECT id FROM messages WHERE room_id = {CacheHarness.AllTalk} ORDER BY created_at DESC, id DESC LIMIT 1");

    static JsonElement JsonRow(string body, long id)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray().First(row => row.GetProperty("id").GetInt64() == id).Clone();
    }
}
