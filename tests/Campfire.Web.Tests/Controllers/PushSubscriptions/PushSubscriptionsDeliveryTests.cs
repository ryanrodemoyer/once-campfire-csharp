using System.Text.Json;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.WebPush;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.PushSubscriptions;

/// <summary>
/// The test-notification action delivers to a local push endpoint. The subscription keys are the
/// reference receiver's (<c>web_push_expected.json</c>); the dial goes to <see cref="LocalPushServer"/>.
/// </summary>
public sealed class PushSubscriptionsDeliveryTests : IAsyncDisposable
{
    const string david = "DavidSessionToken0000001";
    const long davidId = 127326141;
    const string endpoint = "https://fcm.googleapis.com/fcm/send/abc";

    readonly MessagesApp app;
    readonly LocalPushServer server = new();
    readonly WebPushClient push;
    readonly long subscriptionId;

    public PushSubscriptionsDeliveryTests()
    {
        app = new MessagesApp(
            new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
            [
                "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                    $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            ]);
        var (p256dh, auth) = ReceiverKeys();
        var resolver = new ScriptedResolver { Answer = "142.250.185.206" };
        var guard = new PrivateNetworkGuard(resolver);
        push = new WebPushClient(
            server.Handler(guard),
            guard,
            new VapidIdentification(MessagesApp.ParityEnvironment("VAPID_PUBLIC_KEY"), MessagesApp.ParityEnvironment("VAPID_PRIVATE_KEY")),
            app.App.Clock);
        app.App.WebPush = push;
        app.Scalar(
            "INSERT INTO push_subscriptions (user_id, endpoint, p256dh_key, auth_key, user_agent, created_at, updated_at) " +
            $"VALUES ({davidId}, '{endpoint}', '{p256dh}', '{auth}', 'Mozilla/5.0', '2026-03-02 16:00:00', '2026-03-02 16:00:00')");
        subscriptionId = (long)app.Scalar($"SELECT id FROM push_subscriptions WHERE endpoint = '{endpoint}' AND user_id = {davidId}")!;
    }

    [Fact]
    public async Task Test_notification_reaches_a_local_push_endpoint()
    {
        var response = await app.SendAsync("POST", $"/users/me/push_subscriptions/{subscriptionId}/test_notifications", app.SignedIn(david));

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/users/me/push_subscriptions", response.Headers.Location.ToString());
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST /fcm/send/abc HTTP/1.1", request.RequestLine);
        Assert.Equal("fcm.googleapis.com", request.Header("Host"));
    }

    [Fact]
    public async Task A_gone_push_service_is_a_server_error_and_keeps_the_row()
    {
        server.Status = "410 Gone";

        var response = await app.SendAsync("POST", $"/users/me/push_subscriptions/{subscriptionId}/test_notifications", app.SignedIn(david));

        Assert.Equal(500, response.Status);
        Assert.Equal(1L, (long)app.Scalar($"SELECT COUNT(*) FROM push_subscriptions WHERE id = {subscriptionId}")!);
    }

    static (string P256dh, string Auth) ReceiverKeys()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            VectorFiles.Root, "reference-rust", "crates", "campfire", "src", "integrations", "testdata", "web_push_expected.json")));
        return (document.RootElement.GetProperty("p256dh").GetString()!, document.RootElement.GetProperty("auth").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        push.Dispose();
        await server.DisposeAsync();
        app.Dispose();
    }
}
