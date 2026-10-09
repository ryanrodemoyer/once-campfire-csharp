using System.Text.Encodings.Web;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.Searches;

/// <summary>
/// Port of reference/test/controllers/searches_controller_test.rb,
/// on the parity seed with CSRF protection on.
/// </summary>
public sealed class SearchesReferenceTests : IDisposable
{
    const string david = "DavidSessionToken0000001";
    const long davidId = 127326141;
    const long designers = 654632876;

    readonly MessagesApp app = new(
        new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
        [
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        ]);

    async Task<long> CreateMessageAsync(string body = "Hello world!", string clientMessageId = "search")
    {
        var form = $"message%5Bbody%5D={UrlEncoder.Default.Encode(body)}&message%5Bclient_message_id%5D={clientMessageId}";
        var posted = await app.SendAsync("POST", $"/rooms/{designers}/messages", app.SignedIn(david), form);
        Assert.Equal(200, posted.Status);
        return (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = '{clientMessageId}'")!;
    }

    [Fact]
    public async Task Index_initial_view()
    {
        await CreateMessageAsync();

        var response = await app.SendAsync("GET", "/searches", app.SignedIn(david, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.DoesNotContain("class=\"message \"", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finding_reachable_messages()
    {
        await CreateMessageAsync();

        var response = await app.SendAsync("GET", "/searches?q=hello", app.SignedIn(david, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.Contains("Hello world!", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finding_coffee_messages()
    {
        var response = await app.SendAsync("GET", "/searches?q=coffee", app.SignedIn(david, "text/html"));
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task Unreachable_messages_are_not_found()
    {
        await CreateMessageAsync();

        using (var connection = app.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM memberships WHERE user_id = {davidId} AND room_id = {designers}";
            cmd.ExecuteNonQuery();
        }

        var response = await app.SendAsync("GET", "/searches?q=hello", app.SignedIn(david, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.DoesNotContain("Hello world!", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_saves_the_search_term()
    {
        var countBefore = (long)app.Scalar($"SELECT COUNT(*) FROM searches WHERE user_id = {davidId}")!;

        var response = await app.SendAsync("POST", "/searches", app.SignedIn(david), "q=hello");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/searches?q=hello", response.Headers.Location.ToString());

        var countAfter = (long)app.Scalar($"SELECT COUNT(*) FROM searches WHERE user_id = {davidId}")!;
        Assert.Equal(countBefore + 1, countAfter);

        var queryExists = (long)app.Scalar($"SELECT COUNT(*) FROM searches WHERE user_id = {davidId} AND query = 'hello'")! > 0;
        Assert.True(queryExists);
    }

    [Fact]
    public async Task Clear_search_history()
    {
        var countBefore = (long)app.Scalar($"SELECT COUNT(*) FROM searches WHERE user_id = {davidId}")!;
        Assert.True(countBefore > 0);

        var response = await app.SendAsync("DELETE", "/searches/clear", app.SignedIn(david));

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/searches", response.Headers.Location.ToString());

        var countAfter = (long)app.Scalar($"SELECT COUNT(*) FROM searches WHERE user_id = {davidId}")!;
        Assert.Equal(0, countAfter);
    }

    public void Dispose() => app.Dispose();
}
