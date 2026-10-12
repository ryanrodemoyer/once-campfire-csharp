using System.Text.RegularExpressions;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary><c>reference/test/controllers/messages_caching_test.rb</c>.</summary>
public sealed partial class MessagesCachingTests : IDisposable
{
    readonly MessagesApp app = CacheHarness.Open();

    [Fact]
    public async Task Cached_pages_skip_presentation_queries_and_refresh_edited_messages()
    {
        await CacheHarness.WarmAsync(app);
        var path = $"/rooms/{CacheHarness.AllTalk}/messages";
        var first = await CacheHarness.GetAsync(app, path);
        Assert.Equal(200, first.Status);
        var sql = await CacheHarness.QueriesAsync(app, path);
        Assert.Equal(first.Body, (await CacheHarness.GetAsync(app, path)).Body);
        Assert.DoesNotContain(sql, statement => PresentationQuery().IsMatch(statement));

        var message = CacheHarness.Scalar(app, $"SELECT id FROM messages WHERE room_id = {CacheHarness.AllTalk} ORDER BY created_at DESC, id DESC LIMIT 1");
        CacheHarness.Execute(app, $"UPDATE action_text_rich_texts SET body = 'Updated cached message' WHERE record_type = 'Message' AND record_id = {message}");
        var updated = await CacheHarness.GetAsync(app, path);
        Assert.Equal(200, updated.Status);
        Assert.Contains("Updated cached message", updated.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Boosts_are_cached_inside_their_message()
    {
        await CacheHarness.WarmAsync(app);
        var path = $"/rooms/{CacheHarness.AllTalk}/messages";
        var message = CacheHarness.Scalar(app, $"SELECT id FROM messages WHERE room_id = {CacheHarness.AllTalk} ORDER BY created_at DESC, id DESC LIMIT 1");
        CacheHarness.Execute(app, $"""
            INSERT INTO boosts (booster_id, content, created_at, message_id, updated_at)
            VALUES ({CacheHarness.Jason}, '🎉', '2026-03-02 15:30:00', {message}, '2026-03-02 15:30:00')
            """);
        var rendered = await CacheHarness.GetAsync(app, path);
        Assert.Equal(200, rendered.Status);
        Assert.Contains("🎉", rendered.Body, StringComparison.Ordinal);
        app.App.Responses.BudgetOverride = 0;
        app.App.Fragments.Track = true;
        app.App.Fragments.ClearStats();
        await CacheHarness.GetAsync(app, path);
        await CacheHarness.GetAsync(app, path);
        Assert.DoesNotContain(app.App.Fragments.HitKeys, key => key.Contains("messages/boosts/_boost", StringComparison.Ordinal));

        app.App.Responses.BudgetOverride = null;
        var cached = await CacheHarness.GetAsync(app, path);
        app.App.Fragments.Clear();
        var again = await CacheHarness.GetAsync(app, path);
        Assert.Equal(cached.Body, again.Body);
        Assert.Contains("🎉", again.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_render_preloaded_boosts_in_order_without_querying_them_again()
    {
        await CacheHarness.WarmAsync(app);
        var message = CacheHarness.Scalar(app, $"SELECT id FROM messages WHERE room_id = {CacheHarness.AllTalk} ORDER BY created_at DESC, id DESC LIMIT 1");
        CacheHarness.Execute(app, $"""
            INSERT INTO boosts (booster_id, content, created_at, message_id, updated_at)
            VALUES ({CacheHarness.Jason}, '1st', '2026-03-01 12:00:00', {message}, '2026-03-01 12:00:00'),
                   ({CacheHarness.David}, '2nd', '2026-03-02 12:00:00', {message}, '2026-03-02 12:00:00')
            """);
        var earlier = CacheHarness.Scalar(app, $"SELECT id FROM boosts WHERE message_id = {message} AND content = '1st'");
        var later = CacheHarness.Scalar(app, $"SELECT id FROM boosts WHERE message_id = {message} AND content = '2nd'");
        var sql = await CacheHarness.QueriesAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages");
        Assert.DoesNotContain(sql, statement => statement.Contains("ORDER BY \"boosts\"", StringComparison.Ordinal));
        var body = (await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.AllTalk}/messages")).Body;
        var container = body.IndexOf($"id=\"boosts_message_{message}\"", StringComparison.Ordinal);
        Assert.True(container >= 0);
        var first = body.IndexOf($"id=\"boost_{earlier}\"", container, StringComparison.Ordinal);
        var second = body.IndexOf($"id=\"boost_{later}\"", container, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first);
    }

    [Fact]
    public async Task A_page_loads_attachment_previews_with_the_messages()
    {
        await CacheHarness.WarmAsync(app);
        var sql = await CacheHarness.QueriesAsync(app, $"/rooms/{CacheHarness.Designers}/messages");
        Assert.DoesNotContain(sql, statement => PerBlobLookup().IsMatch(statement));
        var page = await CacheHarness.GetAsync(app, $"/rooms/{CacheHarness.Designers}/messages");
        Assert.Equal(200, page.Status);
    }

    public void Dispose() => app.Dispose();

    [GeneratedRegex("action_text_rich_texts|active_storage_attachments|boosts")]
    private static partial Regex PresentationQuery();

    [GeneratedRegex("\"active_storage_\\w+\"\\.\"(id|blob_id|record_id)\" = @")]
    private static partial Regex PerBlobLookup();
}
