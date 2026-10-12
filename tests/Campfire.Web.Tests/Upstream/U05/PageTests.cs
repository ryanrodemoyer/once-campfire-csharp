using System.Text.RegularExpressions;
using Campfire.RailsCompat.Formatting;
using static Campfire.Web.Tests.Upstream.U05.Seed;

namespace Campfire.Web.Tests.Upstream.U05;

/// <summary>
/// The reading pages' upstream changes, at reference edbc779, on the default parity seed:
/// sidebar directs and shared rooms queried apart (0d5998f, users/sidebars_controller_test.rb),
/// one page of account members (0f2a1da, 32b4144, accounts_controller_test.rb), the account
/// settings checkbox (32b4144), notification help on every platform (32b4144,
/// rooms_controller_test.rb), the Edge install icon (4690de5, users/profiles_controller_test.rb),
/// the complete transfer form (27f5461, sessions/transfers_controller_test.rb) and invalid QR code
/// requests (59e1c6d, qr_code_controller_test.rb).
/// </summary>
public sealed partial class PageTests : IDisposable
{
    readonly Controllers.Messages.MessagesApp app = App();

    public void Dispose() => app.Dispose();

    Task<Controllers.Messages.Response> GetAsync(string path, string accept = "text/html") => app.SendAsync("GET", path, app.SignedIn(DavidSession, accept));

    // The reference turns on the partial cache, keyed by the membership, to catch a cached direct
    // whose sort key went stale. The port renders the partial every time, so this keeps the key a
    // cache would have to keep fresh.
    [Fact]
    public async Task A_direct_that_is_already_unread_still_sorts_by_its_latest_message()
    {
        const string jasonSession = "U05JasonSessionToken0001";
        Execute(app, $"INSERT INTO sessions (created_at, last_active_at, token, updated_at, user_id) VALUES ('2026-03-02 16:00:00', '2026-03-02 16:00:00', '{jasonSession}', '2026-03-02 16:00:00', {Jason})");

        await PostAsync(app, DavidAndJason, "First", "998", app.SignedIn(jasonSession));
        // `travel_to 1.hour.ago`: the first message, and what posting it touched, an hour back.
        Execute(app, $"""
            UPDATE messages SET created_at = '2026-03-02 15:00:00', updated_at = '2026-03-02 15:00:00' WHERE room_id = {DavidAndJason} AND client_message_id = '998';
            UPDATE rooms SET updated_at = '2026-03-02 15:00:00' WHERE id = {DavidAndJason};
            UPDATE memberships SET unread_at = '2026-03-02 15:00:00', updated_at = '2026-03-02 15:00:00' WHERE room_id = {DavidAndJason} AND user_id = {David};
            """);
        await GetAsync("/users/me/sidebar");
        Assert.NotNull(app.Scalar($"SELECT unread_at FROM memberships WHERE room_id = {DavidAndJason} AND user_id = {David}"));

        await PostAsync(app, DavidAndJason, "Second", "999", app.SignedIn(jasonSession));
        var body = (await GetAsync("/users/me/sidebar")).Body;

        var updatedAt = DateTimeOffset.Parse((string)app.Scalar($"SELECT updated_at FROM rooms WHERE id = {DavidAndJason}")!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        var link = Regex.Match(body, $"""<a [^>]*id="list_rooms_direct_{DavidAndJason}"[^>]*>""");
        Assert.True(link.Success);
        Assert.Contains($"data-sorted-list-number=\"{TimeFormats.ToFsEpoch(updatedAt)}\"", link.Value, StringComparison.Ordinal);
        Assert.Equal(app.Now, updatedAt);
    }

    [Fact]
    public async Task Directs_are_ordered_by_room_recency_not_name()
    {
        Execute(app, $"UPDATE rooms SET updated_at = '2026-02-28 16:00:00' WHERE id = {DavidAndJason}");
        Execute(app, $"UPDATE rooms SET updated_at = '2026-03-02 15:59:00' WHERE id = {DavidAndKevin}");

        var body = (await GetAsync("/users/me/sidebar")).Body;

        Assert.True(body.IndexOf($"list_rooms_direct_{DavidAndKevin}", StringComparison.Ordinal) < body.IndexOf($"list_rooms_direct_{DavidAndJason}", StringComparison.Ordinal));
    }

    // The reference's fixtures leave none of David's rooms invisible; the seed hides Archive, which
    // the sidebar doesn't list.
    [Fact]
    public async Task Shared_rooms_stay_ordered_by_name()
    {
        var body = (await GetAsync("/users/me/sidebar")).Body;

        var shared = new List<(string Name, string DomId)>();
        using (var connection = app.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT r.name, r.type, r.id FROM rooms r JOIN memberships m ON m.room_id = r.id WHERE m.user_id = {David} AND r.type != 'Rooms::Direct' AND m.involvement != 'invisible'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(1) == "Rooms::Open" ? "open" : "closed";
                shared.Add((reader.GetString(0), $"list_rooms_{type}_{reader.GetInt64(2)}"));
            }
        }
        var positions = shared.OrderBy(room => room.Name.ToLowerInvariant(), StringComparer.Ordinal).Select(room => body.IndexOf(room.DomId, StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(positions);
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public async Task Edit_lists_a_page_of_members_and_the_next_page_carries_on_from_it()
    {
        Execute(app, """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
            INSERT INTO users (name, email_address, created_at, updated_at)
            SELECT 'Member ' || i, 'member' || i || '@37signals.com', '2026-03-02 16:00:00', '2026-03-02 16:00:00' FROM n;
            """);
        Execute(app, $"UPDATE users SET status = 2 WHERE id = {Kevin}");

        var firstPage = ListedUserIds((await GetAsync("/account/edit")).Body);
        var nextPage = ListedUserIds((await GetAsync("/account/users.turbo_stream?page=2", "text/vnd.turbo-stream.html")).Body);

        Assert.NotEmpty(nextPage);
        var visible = new List<long>();
        using (var connection = app.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM users WHERE status IN (0, 2) AND role != 2 ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                visible.Add(reader.GetInt64(0));
            }
        }
        Assert.Equal(visible, firstPage.Concat(nextPage).Order());
    }

    [Fact]
    public async Task Edit_shows_whether_room_creation_is_restricted_to_administrators()
    {
        Assert.Equal("""<input type="checkbox" class="switch__input" data-action="change-&gt;form#submit">""", Switch((await GetAsync("/account/edit")).Body));

        Execute(app, """UPDATE accounts SET settings = '{"restrict_room_creation_to_administrators":true}'""");

        Assert.Equal("""<input type="checkbox" class="switch__input" checked="checked" data-action="change-&gt;form#submit">""", Switch((await GetAsync("/account/edit")).Body));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Android 14; Mobile; rv:131.0) Gecko/131.0 Firefox/131.0")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15")]
    public async Task Show_renders_notification_help_for_each_platform(string userAgent)
    {
        var headers = app.SignedIn(DavidSession, "text/html");
        headers["User-Agent"] = userAgent;

        var response = await app.SendAsync("GET", $"/rooms/{Designers}", headers);

        Assert.Equal(200, response.Status);
        Assert.Matches(NotificationsHelpList(), response.Body);
    }

    [Fact]
    public async Task Profile_includes_install_instructions_for_edge()
    {
        var headers = app.SignedIn(DavidSession, "text/html");
        headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.18363";

        var response = await app.SendAsync("GET", "/users/me/profile", headers);

        Assert.Equal(200, response.Status);
        Assert.Matches(EdgeInstallIcon(), response.Body);
    }

    [Fact]
    public async Task Transfer_page_is_one_complete_auto_submit_form()
    {
        var response = await app.SendAsync("GET", "/session/transfers/some-token", new Dictionary<string, string> { ["Accept"] = "text/html" });

        Assert.Equal(200, response.Status);
        Assert.Single(Regex.Matches(response.Body, """<form [^>]*data-controller="auto-submit"""));
        Assert.Single(Regex.Matches(response.Body, """<input [^>]*name="_method" value="put"|<input [^>]*value="put"[^>]*name="_method"""));
        Assert.Equal(Regex.Count(response.Body, "<form\\b"), Regex.Count(response.Body, "</form>"));
        Assert.Matches(CompleteAutoSubmitForm(), response.Body);
    }

    [Fact]
    public async Task Qr_code_rejects_an_id_that_isnt_base64()
    {
        Assert.Equal(400, (await app.SendAsync("GET", "/qr_code/not-base64!", new Dictionary<string, string>())).Status);
    }

    [Fact]
    public async Task Qr_code_rejects_a_url_too_long_for_a_qr_code()
    {
        var id = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("http://example.com/" + new string('a', 3000))).Replace('+', '-').Replace('/', '_');

        Assert.Equal(400, (await app.SendAsync("GET", $"/qr_code/{id}", new Dictionary<string, string>())).Status);
    }

    // `response.body.scan(%r{href="/users/(\d+)"})`
    static List<long> ListedUserIds(string body) => [.. UserLink().Matches(body).Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];

    static string Switch(string body) => SwitchInput().Match(body).Value;

    [GeneratedRegex("""href="/users/(\d+)(?=")""")]
    private static partial Regex UserLink();

    [GeneratedRegex("""<input type="checkbox" class="switch__input"[^>]*>""")]
    private static partial Regex SwitchInput();

    [GeneratedRegex("""class="[^"]*notifications-help[^"]*"[\s\S]*?<ol>""")]
    private static partial Regex NotificationsHelpList();

    [GeneratedRegex("""class="[^"]*pwa__instructions[^"]*"[\s\S]*?<img [^>]*src="[^"]*install-edge""")]
    private static partial Regex EdgeInstallIcon();

    [GeneratedRegex("""<form\b[^>]*data-controller="auto-submit"[^>]*>(?:\s*<input\b[^>]*>)*\s*</form>""")]
    private static partial Regex CompleteAutoSubmitForm();
}
