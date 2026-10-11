using Campfire.RailsCompat.Cookies;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Upstream.U03;

/// <summary>
/// Port of <c>reference/test/controllers/fetch_metadata_test.rb</c>:
/// verifies Rails' :header_only forgery protection strategy where forms and the layout
/// carry no authenticity token or csrf meta tags, writes are protected with Sec-Fetch-Site
/// and Origin headers, and legacy pre-upgrade cookies/sessions remain valid.
/// </summary>
public sealed class FetchMetadataTests : IDisposable
{
    const string host = "once.campfire.test";
    const long watercoolerRoomId = 486777696;
    const string davidEmail = "david@37signals.com";
    const string password = "secret123456";

    readonly MessagesApp app = new(
        DateTimeOffset.Parse("2026-10-09T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    long SessionCount() => Convert.ToInt64(app.Scalar("SELECT COUNT(*) FROM sessions"));
    long RoomCount() => Convert.ToInt64(app.Scalar("SELECT COUNT(*) FROM rooms"));

    static string FormBody(params (string Key, string Value)[] fields) =>
        string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));

    static string CredentialsBody() =>
        FormBody(("email_address", davidEmail), ("password", password));

    static string? ExtractCookie(Response response, string name)
    {
        if (response.Headers.TryGetValue("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                if (value is not null && value.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return value.Split(';')[0];
                }
            }
        }
        return null;
    }

    [Fact]
    public async Task Login_and_authenticated_forms_have_no_token_fields()
    {
        // get new_session_url
        var newSession = await app.SendAsync("GET", "/session/new", new Dictionary<string, string> { ["Host"] = host });
        Assert.Equal(200, newSession.Status);
        Assert.DoesNotContain("csrf-token", newSession.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("authenticity_token", newSession.Body, StringComparison.Ordinal);

        // sign_in :david
        var signIn = await app.SendAsync(
            "POST",
            "/session",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Origin"] = $"http://{host}",
            },
            CredentialsBody());
        Assert.Equal(302, signIn.Status);
        var sessionTokenCookie = ExtractCookie(signIn, "session_token");
        Assert.NotNull(sessionTokenCookie);

        // get room_url(rooms(:watercooler))
        var roomResponse = await app.SendAsync(
            "GET",
            $"/rooms/{watercoolerRoomId}",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Cookie"] = sessionTokenCookie,
            });
        Assert.Equal(200, roomResponse.Status);
        Assert.DoesNotContain("csrf-token", roomResponse.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("authenticity_token", roomResponse.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Https_login_accepts_browser_metadata_without_a_token()
    {
        foreach (var site in new[] { "same-origin", "same-site" })
        {
            var response = await app.SendAsync(
                "POST",
                "/session",
                new Dictionary<string, string>
                {
                    ["Host"] = host,
                    ["Origin"] = $"https://{host}",
                    ["Sec-Fetch-Site"] = site,
                },
                CredentialsBody(),
                scheme: "https");

            Assert.Equal(302, response.Status);
            var sessionTokenCookie = ExtractCookie(response, "session_token");
            Assert.NotNull(sessionTokenCookie);
        }
    }

    [Fact]
    public async Task Https_writes_reject_missing_metadata_even_with_a_legacy_token_parameter()
    {
        var before = SessionCount();
        var response = await app.SendAsync(
            "POST",
            "/session",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Origin"] = $"https://{host}",
            },
            FormBody(("email_address", davidEmail), ("password", password), ("authenticity_token", "old token")),
            scheme: "https");

        Assert.Equal(422, response.Status);
        Assert.Equal(before, SessionCount());
    }

    [Fact]
    public async Task Cross_site_none_and_malformed_metadata_cannot_sign_in_over_http_or_https()
    {
        foreach (var tls in new[] { false, true })
        {
            var scheme = tls ? "https" : "http";
            foreach (var site in new[] { "cross-site", "none", "garbage" })
            {
                var before = SessionCount();
                var response = await app.SendAsync(
                    "POST",
                    "/session",
                    new Dictionary<string, string>
                    {
                        ["Host"] = host,
                        ["Sec-Fetch-Site"] = site,
                    },
                    CredentialsBody(),
                    scheme: scheme);

                Assert.Equal(422, response.Status);
                Assert.Equal(before, SessionCount());
            }
        }
    }

    [Fact]
    public async Task Provided_foreign_and_null_origins_fail_even_with_same_site_metadata()
    {
        foreach (var origin in new[] { "https://foreign.example", "null" })
        {
            var before = SessionCount();
            var response = await app.SendAsync(
                "POST",
                "/session",
                new Dictionary<string, string>
                {
                    ["Host"] = host,
                    ["Sec-Fetch-Site"] = "same-site",
                    ["Origin"] = origin,
                },
                CredentialsBody(),
                scheme: "https");

            Assert.Equal(422, response.Status);
            Assert.Equal(before, SessionCount());
        }
    }

    [Fact]
    public async Task Plain_http_retains_the_missing_header_fallback()
    {
        var response = await app.SendAsync(
            "POST",
            "/session",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Origin"] = $"http://{host}",
            },
            CredentialsBody(),
            scheme: "http");

        Assert.Equal(302, response.Status);
        var sessionTokenCookie = ExtractCookie(response, "session_token");
        Assert.NotNull(sessionTokenCookie);
    }

    [Fact]
    public async Task Cross_site_reads_remain_available_and_method_overridden_writes_stay_protected()
    {
        var getNewSession = await app.SendAsync(
            "GET",
            "/session/new",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Sec-Fetch-Site"] = "cross-site",
            });
        Assert.Equal(200, getNewSession.Status);

        // Sign in David to obtain session cookie
        var signIn = await app.SendAsync(
            "POST",
            "/session",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Origin"] = $"http://{host}",
            },
            CredentialsBody());
        Assert.Equal(302, signIn.Status);
        var sessionTokenCookie = ExtractCookie(signIn, "session_token");
        Assert.NotNull(sessionTokenCookie);

        // Method-overridden write: POST /rooms/486777696 with _method=delete and Sec-Fetch-Site: cross-site
        var before = RoomCount();
        var deleteResponse = await app.SendAsync(
            "POST",
            $"/rooms/{watercoolerRoomId}",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Cookie"] = sessionTokenCookie,
                ["Sec-Fetch-Site"] = "cross-site",
            },
            FormBody(("_method", "delete")));

        Assert.Equal(422, deleteResponse.Status);
        Assert.Equal(before, RoomCount());
    }

    [Fact]
    public async Task Pre_upgrade_rails_issued_cookie_with_csrf_token_passes()
    {
        // Pre-upgrade Rails session cookies carry an encrypted _campfire_session cookie containing
        // {"session_id": "...", "_csrf_token": "..."} alongside the signed session_token.
        // These cookies must decrypt, preserve session authenticity, and authenticate correctly.
        var preUpgradeVector = RailsCompatVectors.File.Session;
        var preUpgradeSessionCookie = preUpgradeVector.SetCookie.Split(';')[0];

        // David's active session token in the parity seed database
        const string davidSeedToken = "AxJs94fteQ5Autv2VrKsH68c";

        var jar = new CookieJar(app.Keys, () => app.Now);
        jar.SetAuthenticationCookie(davidSeedToken);
        var sessionTokenCookie = jar.ToSetCookieHeaders().First(h => h.StartsWith("session_token=", StringComparison.Ordinal)).Split(';')[0];

        var cookies = $"{sessionTokenCookie}; {preUpgradeSessionCookie}";

        // Authenticated GET request using the pre-upgrade cookie succeeds
        var roomResponse = await app.SendAsync(
            "GET",
            $"/rooms/{watercoolerRoomId}",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Cookie"] = cookies,
            });

        Assert.Equal(200, roomResponse.Status);
        Assert.DoesNotContain("csrf-token", roomResponse.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("authenticity_token", roomResponse.Body, StringComparison.Ordinal);

        // Authenticated POST request with header-only metadata succeeds with pre-upgrade session
        var postResponse = await app.SendAsync(
            "POST",
            $"/rooms/{watercoolerRoomId}/messages",
            new Dictionary<string, string>
            {
                ["Host"] = host,
                ["Cookie"] = cookies,
                ["Origin"] = $"http://{host}",
                ["Sec-Fetch-Site"] = "same-origin",
                ["Accept"] = "text/vnd.turbo-stream.html",
            },
            FormBody(("message[body]", "Hello from pre-upgrade session")));

        Assert.Equal(200, postResponse.Status);
    }

    public void Dispose() => app.Dispose();
}
