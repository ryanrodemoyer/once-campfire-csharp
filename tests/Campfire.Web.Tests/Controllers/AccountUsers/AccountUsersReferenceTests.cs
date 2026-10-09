using Campfire.Data.Events;
using Campfire.Jobs;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.AccountUsers;

/// <summary>
/// reference/test/controllers/accounts/users_controller_test.rb and
/// users/bans_controller_test.rb, on the parity seed with CSRF protection on (the token sent as
/// Turbo sends it). David administers; Kevin and JZ are members.
/// </summary>
public sealed class AccountUsersReferenceTests : IDisposable
{
    const string david = "DavidSessionToken0000001";
    const string kevin = "KevinSessionToken0000002";
    const long davidId = 127326141;
    const long kevinId = 712064548;
    const long jzId = 773523953;

    readonly MessagesApp app = new(
        new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
        [
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900002, {kevinId}, '{kevin}', '198.51.100.8', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        ]);

    [Fact]
    public async Task Update()
    {
        Assert.Equal(1L, Role(davidId));

        var response = await app.SendAsync("PUT", $"/account/users/{davidId}", app.SignedIn(david), "user%5Brole%5D=administrator");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/edit", response.Headers.Location.ToString());
        Assert.Equal(1L, Role(davidId));
    }

    [Fact]
    public async Task Destroy()
    {
        var active = ActiveCount();

        var response = await app.SendAsync("DELETE", $"/account/users/{davidId}", app.SignedIn(david), "");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/account/edit", response.Headers.Location.ToString());
        Assert.Equal(active - 1, ActiveCount());
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM users WHERE status = 0 AND id = {davidId}"));
    }

    [Fact]
    public async Task Non_admins_cannot_perform_actions()
    {
        Assert.Equal(403, (await app.SendAsync("PUT", $"/account/users/{davidId}", app.SignedIn(kevin), "user%5Brole%5D=administrator")).Status);
        Assert.Equal(403, (await app.SendAsync("DELETE", $"/account/users/{davidId}", app.SignedIn(kevin), "")).Status);
    }

    [Fact]
    public async Task Create_bans_user_and_creates_ban_records_from_sessions()
    {
        AddSession(kevinId, "203.0.113.1");
        AddSession(kevinId, "203.0.113.2");
        var bans = Count("bans");

        var response = await app.SendAsync("POST", $"/users/{kevinId}/ban", app.SignedIn(david), "");

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://campfire.test/users/{kevinId}", response.Headers.Location.ToString());
        // Kevin's own session, from 198.51.100.8, is banned too.
        Assert.Equal(bans + 3, Count("bans"));
        Assert.Equal(1L, app.Scalar($"SELECT COUNT(*) FROM bans WHERE ip_address = '203.0.113.1' AND user_id = {kevinId}"));
        Assert.Equal(1L, app.Scalar($"SELECT COUNT(*) FROM bans WHERE ip_address = '203.0.113.2' AND user_id = {kevinId}"));
    }

    [Fact]
    public async Task Create_destroys_user_sessions()
    {
        AddSession(kevinId, "203.0.113.1");
        Assert.Equal(2L, Sessions(kevinId));

        await app.SendAsync("POST", $"/users/{kevinId}/ban", app.SignedIn(david), "");

        Assert.Equal(0L, Sessions(kevinId));
    }

    [Fact]
    public async Task Create_enqueues_RemoveBannedContentJob()
    {
        await app.SendAsync("POST", $"/users/{kevinId}/ban", app.SignedIn(david), "");

        Assert.Contains(new RemoveBannedContentJob(kevinId), app.Seams.Jobs);
    }

    [Fact]
    public async Task RemoveBannedContentJob_deletes_messages()
    {
        AddSession(kevinId, "203.0.113.1");
        Assert.NotEqual(0L, app.Scalar($"SELECT COUNT(*) FROM messages WHERE creator_id = {kevinId}"));

        await app.SendAsync("POST", $"/users/{kevinId}/ban", app.SignedIn(david), "");
        var job = new RemoveBannedContent(app.Database, app.Seams, app.App.Clock);
        foreach (var enqueued in app.Seams.Jobs.OfType<RemoveBannedContentJob>())
        {
            await job.PerformAsync(enqueued, CancellationToken.None);
        }

        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM messages WHERE creator_id = {kevinId}"));
    }

    [Fact]
    public async Task Non_admins_cannot_ban_users()
    {
        Assert.Equal(403, (await app.SendAsync("POST", $"/users/{jzId}/ban", app.SignedIn(kevin), "")).Status);
    }

    [Fact]
    public async Task Destroy_removes_ban_records_and_sets_user_to_active()
    {
        AddSession(kevinId, "203.0.113.1");
        await app.SendAsync("POST", $"/users/{kevinId}/ban", app.SignedIn(david), "");
        Assert.Equal(2L, Status(kevinId));
        var bans = Count("bans");

        var response = await app.SendAsync("DELETE", $"/users/{kevinId}/ban", app.SignedIn(david), "");

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://campfire.test/users/{kevinId}", response.Headers.Location.ToString());
        Assert.Equal(bans - 2, Count("bans"));
        Assert.Equal(0L, Status(kevinId));
    }

    [Fact]
    public async Task Non_admins_cannot_unban_users()
    {
        app.Scalar($"UPDATE users SET status = 2 WHERE id = {jzId}");

        Assert.Equal(403, (await app.SendAsync("DELETE", $"/users/{jzId}/ban", app.SignedIn(kevin), "")).Status);
        Assert.Equal(2L, Status(jzId));
    }

    [Fact]
    public async Task Requests_without_a_CSRF_token_are_refused()
    {
        var headers = app.SignedIn(david);
        headers.Remove("X-CSRF-Token");

        Assert.Equal(422, (await app.SendAsync("POST", $"/users/{kevinId}/ban", headers, "")).Status);
        Assert.Equal(422, (await app.SendAsync("PATCH", $"/account/users/{kevinId}", headers, "user%5Brole%5D=administrator")).Status);
        Assert.Equal(0L, Status(kevinId));
        Assert.Equal(0L, Role(kevinId));
    }

    [Fact]
    public async Task The_next_page_is_a_turbo_stream_of_people()
    {
        var response = await app.SendAsync("GET", "/account/users?format=turbo_stream", app.SignedIn(kevin, "text/html, application/xhtml+xml"));

        Assert.Equal(200, response.Status);
        Assert.Equal("text/vnd.turbo-stream.html; charset=utf-8", response.Headers.ContentType.ToString());
        Assert.StartsWith("<turbo-stream action=\"replace\" target=\"next_page_container\"><template>", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", response.Body, StringComparison.Ordinal);
        // A member sees no role or removal controls.
        Assert.DoesNotContain("/account/users/", response.Body, StringComparison.Ordinal);
    }

    void AddSession(long userId, string ipAddress) => app.Scalar(
        "INSERT INTO sessions (user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
        $"VALUES ({userId}, '{Guid.NewGuid():N}', '{ipAddress}', 'Test', '2026-03-02 16:00:00', '2026-03-02 16:00:00', '2026-03-02 16:00:00')");

    long Count(string table) => (long)app.Scalar($"SELECT COUNT(*) FROM {table}")!;

    long ActiveCount() => (long)app.Scalar("SELECT COUNT(*) FROM users WHERE status = 0")!;

    long Sessions(long userId) => (long)app.Scalar($"SELECT COUNT(*) FROM sessions WHERE user_id = {userId}")!;

    long Role(long userId) => (long)app.Scalar($"SELECT role FROM users WHERE id = {userId}")!;

    long Status(long userId) => (long)app.Scalar($"SELECT status FROM users WHERE id = {userId}")!;

    public void Dispose() => app.Dispose();
}
