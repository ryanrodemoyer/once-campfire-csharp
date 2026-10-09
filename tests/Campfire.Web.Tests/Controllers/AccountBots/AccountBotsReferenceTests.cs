using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.AccountBots;

/// <summary>
/// Port of reference/test/controllers/accounts/bots_controller_test.rb,
/// reference/test/controllers/accounts/bots/keys_controller_test.rb, and
/// reference/test/models/user/bot_test.rb, on the parity seed with CSRF protection on.
/// David administers; Kevin is a member; Bender Bot (394959859) and Deploy Bot (773523956)
/// are active bots; Old Bot (773523957) is a deactivated bot.
/// </summary>
public sealed class AccountBotsReferenceTests : IDisposable
{
    const string david = "DavidSessionToken0000001";
    const string kevin = "KevinSessionToken0000002";
    const long davidId = 127326141;
    const long kevinId = 712064548;
    const long benderId = 394959859;
    const long oldBotId = 773523957;

    readonly MessagesApp app = new(
        new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
        [
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900002, {kevinId}, '{kevin}', '198.51.100.8', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        ]);

    [Fact]
    public async Task Index()
    {
        var response = await app.SendAsync("GET", "/account/bots", app.SignedIn(david));

        Assert.Equal(200, response.Status);
        Assert.Contains("Chat bots", response.Body, StringComparison.Ordinal);
        Assert.Contains("Bender Bot", response.Body, StringComparison.Ordinal);
        Assert.Contains("Deploy Bot", response.Body, StringComparison.Ordinal);
        Assert.Contains($"http://campfire.test/rooms/486777696/{benderId}-BenderBot123/messages", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create()
    {
        var newResponse = await app.SendAsync("GET", "/account/bots/new", app.SignedIn(david));
        Assert.Equal(200, newResponse.Status);
        Assert.Contains("New chat bot", newResponse.Body, StringComparison.Ordinal);

        var botsBefore = ActiveBotsCount();
        var response = await app.SendAsync(
            "POST",
            "/account/bots",
            app.SignedIn(david),
            "user%5Bname%5D=Bender%27s+Friend");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/bots", response.Headers.Location.ToString());
        Assert.Equal(botsBefore + 1, ActiveBotsCount());

        var (name, role, status, botToken) = LastBot();
        Assert.Equal("Bender's Friend", name);
        Assert.Equal(2L, role); // UserRole.Bot
        Assert.Equal(0L, status); // UserStatus.Active
        Assert.NotNull(botToken);
        Assert.Equal(12, botToken.Length);
    }

    [Fact]
    public async Task Create_with_webhook()
    {
        var webhooksBefore = WebhooksCount();

        var response = await app.SendAsync(
            "POST",
            "/account/bots",
            app.SignedIn(david),
            "user%5Bname%5D=Webhook+Bot&user%5Bwebhook_url%5D=https%3A%2F%2Fexample.com%2Fhook");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/bots", response.Headers.Location.ToString());
        Assert.Equal(webhooksBefore + 1, WebhooksCount());

        var (botId, _) = LastBotIdAndToken();
        var webhookUrl = (string?)app.Scalar($"SELECT url FROM webhooks WHERE user_id = {botId}");
        Assert.Equal("https://example.com/hook", webhookUrl);
    }

    [Fact]
    public async Task Update()
    {
        var editResponse = await app.SendAsync("GET", $"/account/bots/{benderId}/edit", app.SignedIn(david));
        Assert.Equal(200, editResponse.Status);
        Assert.Contains("Edit bot", editResponse.Body, StringComparison.Ordinal);
        Assert.Contains("Bender Bot", editResponse.Body, StringComparison.Ordinal);

        var response = await app.SendAsync(
            "PUT",
            $"/account/bots/{benderId}",
            app.SignedIn(david),
            "user%5Bname%5D=Bender%27s+New+Friend");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/bots", response.Headers.Location.ToString());
        Assert.Equal("Bender's New Friend", (string)app.Scalar($"SELECT name FROM users WHERE id = {benderId}")!);
    }

    [Fact]
    public async Task Destroy()
    {
        var activeBotsBefore = ActiveBotsCount();

        var response = await app.SendAsync("DELETE", $"/account/bots/{benderId}", app.SignedIn(david), "");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/bots", response.Headers.Location.ToString());
        Assert.Equal(activeBotsBefore - 1, ActiveBotsCount());
        Assert.Equal(1L, (long)app.Scalar($"SELECT status FROM users WHERE id = {benderId}")!); // Deactivated
    }

    [Fact]
    public async Task Remove_webhook()
    {
        Assert.Equal(1L, (long)app.Scalar($"SELECT COUNT(*) FROM webhooks WHERE user_id = {benderId}")!);
        var webhooksBefore = WebhooksCount();

        var response = await app.SendAsync(
            "PUT",
            $"/account/bots/{benderId}",
            app.SignedIn(david),
            "user%5Bname%5D=Bender%27s+New+Friend&user%5Bwebhook_url%5D=");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/bots", response.Headers.Location.ToString());
        Assert.Equal(webhooksBefore - 1, WebhooksCount());
        Assert.Equal(0L, (long)app.Scalar($"SELECT COUNT(*) FROM webhooks WHERE user_id = {benderId}")!);
    }

    [Fact]
    public async Task Keys_update()
    {
        var initialToken = (string)app.Scalar($"SELECT bot_token FROM users WHERE id = {benderId}")!;

        var response = await app.SendAsync("PUT", $"/account/bots/{benderId}/key", app.SignedIn(david), "");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/bots", response.Headers.Location.ToString());

        var newToken = (string)app.Scalar($"SELECT bot_token FROM users WHERE id = {benderId}")!;
        Assert.NotEqual(initialToken, newToken);
        Assert.Equal(12, newToken.Length);
    }

    [Fact]
    public async Task Bot_token_and_bot_key_format()
    {
        var token = User.GenerateBotToken();
        Assert.Equal(12, token.Length);
        Assert.All(token, c => Assert.True(char.IsLetterOrDigit(c)));

        var bot = await app.Database.ReadAsync(session => Users.FindActiveBot(session, benderId)!, TestContext.Current.CancellationToken);
        Assert.Equal($"{benderId}-{bot.BotToken}", bot.BotKey);

        // AuthenticateBot matches reference format "#{id}-#{bot_token}"
        var authenticated = await app.Database.ReadAsync(session => Users.AuthenticateBot(session, bot.BotKey), TestContext.Current.CancellationToken);
        Assert.NotNull(authenticated);
        Assert.Equal(benderId, authenticated.Id);

        // AuthenticateBot returns null on bad token, wrong id, or invalid format
        Assert.Null(await app.Database.ReadAsync(session => Users.AuthenticateBot(session, $"{benderId}-wrongtoken"), TestContext.Current.CancellationToken));
        Assert.Null(await app.Database.ReadAsync(session => Users.AuthenticateBot(session, $"999999-{bot.BotToken}"), TestContext.Current.CancellationToken));
        Assert.Null(await app.Database.ReadAsync(session => Users.AuthenticateBot(session, "invalid"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Non_admins_cannot_perform_actions()
    {
        Assert.Equal(403, (await app.SendAsync("GET", "/account/bots", app.SignedIn(kevin))).Status);
        Assert.Equal(403, (await app.SendAsync("GET", "/account/bots/new", app.SignedIn(kevin))).Status);
        Assert.Equal(403, (await app.SendAsync("POST", "/account/bots", app.SignedIn(kevin), "user%5Bname%5D=HackerBot")).Status);
        Assert.Equal(403, (await app.SendAsync("GET", $"/account/bots/{benderId}/edit", app.SignedIn(kevin))).Status);
        Assert.Equal(403, (await app.SendAsync("PUT", $"/account/bots/{benderId}", app.SignedIn(kevin), "user%5Bname%5D=Hacked")).Status);
        Assert.Equal(403, (await app.SendAsync("DELETE", $"/account/bots/{benderId}", app.SignedIn(kevin), "")).Status);
        Assert.Equal(403, (await app.SendAsync("PUT", $"/account/bots/{benderId}/key", app.SignedIn(kevin), "")).Status);
    }

    [Fact]
    public async Task Requests_without_CSRF_token_are_refused()
    {
        var headers = app.SignedIn(david);
        headers.Remove("X-CSRF-Token");

        Assert.Equal(422, (await app.SendAsync("POST", "/account/bots", headers, "user%5Bname%5D=Forged")).Status);
        Assert.Equal(422, (await app.SendAsync("PUT", $"/account/bots/{benderId}", headers, "user%5Bname%5D=Forged")).Status);
        Assert.Equal(422, (await app.SendAsync("DELETE", $"/account/bots/{benderId}", headers, "")).Status);
        Assert.Equal(422, (await app.SendAsync("PUT", $"/account/bots/{benderId}/key", headers, "")).Status);
    }

    [Fact]
    public async Task Show_route_returns_not_found()
    {
        // Rails does not implement show on Accounts::BotsController; returns 404 ActionNotFound
        var response = await app.SendAsync("GET", $"/account/bots/{benderId}", app.SignedIn(david));
        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Missing_or_deactivated_bots_return_not_found()
    {
        const long missingId = 999999999;
        Assert.Equal(404, (await app.SendAsync("GET", $"/account/bots/{missingId}/edit", app.SignedIn(david))).Status);
        Assert.Equal(404, (await app.SendAsync("PUT", $"/account/bots/{missingId}", app.SignedIn(david), "user%5Bname%5D=Missing")).Status);
        Assert.Equal(404, (await app.SendAsync("DELETE", $"/account/bots/{missingId}", app.SignedIn(david), "")).Status);
        Assert.Equal(404, (await app.SendAsync("PUT", $"/account/bots/{missingId}/key", app.SignedIn(david), "")).Status);

        // Deactivated bot
        Assert.Equal(404, (await app.SendAsync("GET", $"/account/bots/{oldBotId}/edit", app.SignedIn(david))).Status);
        Assert.Equal(404, (await app.SendAsync("PUT", $"/account/bots/{oldBotId}", app.SignedIn(david), "user%5Bname%5D=Old")).Status);
        Assert.Equal(404, (await app.SendAsync("DELETE", $"/account/bots/{oldBotId}", app.SignedIn(david), "")).Status);
        Assert.Equal(404, (await app.SendAsync("PUT", $"/account/bots/{oldBotId}/key", app.SignedIn(david), "")).Status);
    }

    long ActiveBotsCount() => (long)app.Scalar("SELECT COUNT(*) FROM users WHERE role = 2 AND status = 0")!;

    long WebhooksCount() => (long)app.Scalar("SELECT COUNT(*) FROM webhooks")!;

    (string Name, long Role, long Status, string? BotToken) LastBot()
    {
        using var connection = app.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, role, status, bot_token FROM users WHERE role = 2 ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    (long Id, string? BotToken) LastBotIdAndToken()
    {
        using var connection = app.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, bot_token FROM users WHERE role = 2 ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public void Dispose() => app.Dispose();
}
