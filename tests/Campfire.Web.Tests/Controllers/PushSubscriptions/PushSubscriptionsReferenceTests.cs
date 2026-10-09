using System.Text.Encodings.Web;
using Campfire.Jobs.WebPush;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.PushSubscriptions;

/// <summary>
/// Port of reference/test/controllers/users/push_subscriptions_controller_test.rb,
/// on the parity seed with CSRF protection on. DNS is the oracle's stub: a public address,
/// unless a case says otherwise.
/// </summary>
public sealed class PushSubscriptionsReferenceTests : IDisposable
{
    const string david = "DavidSessionToken0000001";
    const long davidId = 127326141;
    const long chromeId = 56887440;

    readonly ScriptedResolver resolver = new();
    readonly MessagesApp app;
    readonly WebPushClient push;

    public PushSubscriptionsReferenceTests()
    {
        app = new MessagesApp(
            new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
            [
                "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                    $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            ]);
        push = PushSubscriptionClients.Open(resolver, app);
        app.App.WebPush = push;
    }

    [Fact]
    public async Task Create_new_push_subscription()
    {
        var before = CountForDavid();
        var headers = app.SignedIn(david);
        headers["User-Agent"] = "Mozilla/5.0";

        var response = await app.SendAsync("POST", "/users/me/push_subscriptions", headers,
            Form("https://fcm.googleapis.com/fcm/send/abc123", "123", "456"));

        Assert.Equal(200, response.Status);
        Assert.Equal(before + 1, CountForDavid());
        var id = (long)app.Scalar($"SELECT MAX(id) FROM push_subscriptions WHERE user_id = {davidId}")!;
        Assert.Equal("https://fcm.googleapis.com/fcm/send/abc123", Column(id, "endpoint"));
        Assert.Equal("123", Column(id, "p256dh_key"));
        Assert.Equal("456", Column(id, "auth_key"));
        Assert.Equal("Mozilla/5.0", Column(id, "user_agent"));
    }

    [Fact]
    public async Task Touch_existing_subscription()
    {
        var before = CountAll();
        Assert.NotEqual("2026-03-02 16:00:00", Column(chromeId, "updated_at"));

        var response = await app.SendAsync("POST", "/users/me/push_subscriptions", app.SignedIn(david),
            Form(Column(chromeId, "endpoint"), Column(chromeId, "p256dh_key"), Column(chromeId, "auth_key")));

        Assert.Equal(200, response.Status);
        Assert.Equal(before, CountAll());
        Assert.Equal("2026-03-02 16:00:00", Column(chromeId, "updated_at"));
    }

    [Fact]
    public async Task Rejects_subscription_with_non_permitted_endpoint()
    {
        var before = CountAll();

        var response = await app.SendAsync("POST", "/users/me/push_subscriptions", app.SignedIn(david),
            Form("https://attacker.example.com/steal", "123", "456"));

        Assert.Equal(422, response.Status);
        Assert.Equal(before, CountAll());
    }

    [Fact]
    public async Task Rejects_subscription_with_endpoint_resolving_to_a_private_ip()
    {
        resolver.Answer = "169.254.169.254";
        var before = CountAll();

        var response = await app.SendAsync("POST", "/users/me/push_subscriptions", app.SignedIn(david),
            Form("https://fcm.googleapis.com/fcm/send/abc123", "123", "456"));

        Assert.Equal(422, response.Status);
        Assert.Equal(before, CountAll());
    }

    [Fact]
    public async Task Re_registering_a_legacy_invalid_subscription_is_rejected_with_422()
    {
        app.Scalar(
            "INSERT INTO push_subscriptions (user_id, endpoint, p256dh_key, auth_key, user_agent, created_at, updated_at) " +
            $"VALUES ({davidId}, 'https://attacker.example.com/steal', '123', '456', 'Mozilla/5.0', '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        var before = CountAll();

        var response = await app.SendAsync("POST", "/users/me/push_subscriptions", app.SignedIn(david),
            Form("https://attacker.example.com/steal", "123", "456"));

        Assert.Equal(422, response.Status);
        Assert.Equal(before, CountAll());
    }

    [Fact]
    public async Task Destroy_a_push_subscription()
    {
        var before = CountAll();

        var response = await app.SendAsync("DELETE", $"/users/me/push_subscriptions/{chromeId}", app.SignedIn(david));

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/users/me/push_subscriptions", response.Headers.Location.ToString());
        Assert.Equal(before - 1, CountAll());
        Assert.Null(app.Scalar($"SELECT id FROM push_subscriptions WHERE id = {chromeId}"));
    }

    static string Form(string endpoint, string p256dh, string auth) =>
        "push_subscription%5Bendpoint%5D=" + UrlEncoder.Default.Encode(endpoint) +
        "&push_subscription%5Bp256dh_key%5D=" + UrlEncoder.Default.Encode(p256dh) +
        "&push_subscription%5Bauth_key%5D=" + UrlEncoder.Default.Encode(auth);

    long CountAll() => (long)app.Scalar("SELECT COUNT(*) FROM push_subscriptions")!;

    long CountForDavid() => (long)app.Scalar($"SELECT COUNT(*) FROM push_subscriptions WHERE user_id = {davidId}")!;

    string Column(long id, string column) => (string)app.Scalar($"SELECT {column} FROM push_subscriptions WHERE id = {id}")!;

    public void Dispose()
    {
        push.Dispose();
        app.Dispose();
    }
}
