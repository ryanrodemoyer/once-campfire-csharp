using System.Net;
using System.Text;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.Tests.RestrictedHttp;
using Campfire.Jobs.WebPush;

namespace Campfire.Jobs.Tests.WebPush;

// WebPush::Notification#deliver through the pinned branch of WebPush::PersistentRequest
// (reference/config/initializers/web_push.rb), over TLS to a loopback push service, and the
// delivery tests of reference/test/models/push/subscription_test.rb and
// reference/test/lib/web_push/persistent_request_test.rb.
public sealed class WebPushDeliveryTests : IAsyncDisposable
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    readonly PushServiceServer server = new();

    static WebPushNotification Notification(string endpoint = ReferenceVector.Endpoint) => new(
        ReferenceVector.Title, ReferenceVector.Body, ReferenceVector.RoomPath, ReferenceVector.Badge,
        endpoint, ReferenceVector.P256dh, ReferenceVector.Auth);

    WebPushClient Client(FakeResolver resolver, bool trusted = true)
    {
        var guard = new PrivateNetworkGuard(resolver);
        return new WebPushClient(server.Handler(guard, trusted), guard,
            new VapidIdentification(ReferenceVector.VapidPublicKey, ReferenceVector.VapidPrivateKey), TimeProvider.System);
    }

    [Fact]
    public async Task Delivers_a_payload_the_reference_receiver_decrypts_to_the_pinned_address()
    {
        var resolver = FakeResolver.Of(PushEndpointTests.PublicTestIp);
        using var client = Client(resolver);

        Assert.Equal(HttpStatusCode.Created, await Notification().DeliverAsync(client, Cancellation));

        Assert.Equal([new IPEndPoint(IPAddress.Parse(PushEndpointTests.PublicTestIp), 443)], server.Dialled);
        Assert.Equal(["fcm.googleapis.com"], resolver.Lookups);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST /fcm/send/abc HTTP/1.1", request.RequestLine);
        Assert.Equal("fcm.googleapis.com", request.Header("Host"));
        foreach (var (name, value) in ReferenceVector.Headers)
        {
            Assert.Equal(value, request.Header(name));
        }
        Assert.Matches("^vapid t=[^.]+\\.[^.]+\\.[^.,]+,k=" + ReferenceVector.String("authorization_k") + "$", request.Header("Authorization"));
        var (recordSize, plaintext) = ReferenceVector.Receiver.Decrypt(request.Body);
        Assert.Equal((uint)request.Body.Length - 86, recordSize);
        Assert.Equal(ReferenceVector.Message + "\u0002\u0000", Encoding.UTF8.GetString(plaintext));
    }

    // "endpoint resolution is deferred from the enqueue path to the delivery worker" and
    // "delivery is skipped when the endpoint no longer resolves to a public IP"
    [Fact]
    public async Task Delivery_resolves_again_and_is_skipped_when_the_endpoint_turned_private()
    {
        var resolver = FakeResolver.Sequence([PushEndpointTests.PublicTestIp], ["10.0.0.5"]);
        using var client = Client(resolver);
        var notification = Notification();
        Assert.Empty(resolver.Lookups);

        Assert.NotNull(await notification.DeliverAsync(client, Cancellation));
        Assert.Null(await notification.DeliverAsync(client, Cancellation));

        Assert.Equal(2, resolver.Lookups.Count);
        Assert.Single(server.Requests);
        Assert.Single(server.Dialled);
    }

    [Theory]
    [InlineData("https://attacker.example.com/collect")]
    [InlineData("https://fcm.googleapis.com:22/fcm/send/abc123")]
    public async Task Delivery_is_skipped_for_an_endpoint_that_is_not_permitted(string endpoint)
    {
        var resolver = FakeResolver.Of(PushEndpointTests.PublicTestIp);
        using var client = Client(resolver);

        Assert.Null(await Notification(endpoint).DeliverAsync(client, Cancellation));

        Assert.Empty(resolver.Lookups);
        Assert.Empty(server.Dialled);
    }

    [Fact]
    public async Task A_gone_subscription_raises_ExpiredSubscription()
    {
        server.Status = "410 Gone";
        using var client = Client(FakeResolver.Of(PushEndpointTests.PublicTestIp));

        var error = await Assert.ThrowsAsync<WebPushExpiredSubscriptionException>(() => Notification().DeliverAsync(client, Cancellation));

        Assert.Equal("fcm.googleapis.com", error.Host);
    }

    [Fact]
    public async Task Fcms_unauthorized_registration_reason_is_read_from_the_status_line()
    {
        server.Status = "400 UnauthorizedRegistration";
        using var client = Client(FakeResolver.Of(PushEndpointTests.PublicTestIp));

        var error = await Assert.ThrowsAsync<WebPushResponseException>(() => Notification().DeliverAsync(client, Cancellation));

        Assert.Equal("WebPush::Unauthorized", error.RubyClass);
    }

    // OpenSSL::SSL::SSLError, which the pool treats as a dead subscription.
    [Fact]
    public async Task A_failed_TLS_session_is_an_OpenSSL_error()
    {
        using var client = Client(FakeResolver.Of(PushEndpointTests.PublicTestIp), trusted: false);

        var error = await Assert.ThrowsAsync<WebPushOpenSslException>(() => Notification().DeliverAsync(client, Cancellation));

        Assert.Equal("OpenSSL::SSL::SSLError", error.RubyClass);
        Assert.Empty(server.Requests);
    }

    // persistent_request_test.rb: "pins delivery to endpoint_ip instead of re-resolving the host",
    // with an empty message, which skips encryption.
    [Fact]
    public async Task Payload_send_connects_to_the_endpoint_ip_without_a_lookup()
    {
        var resolver = FakeResolver.Of("127.0.0.1");
        using var client = Client(resolver);

        await client.PayloadSendAsync("", "https://fcm.googleapis.com/fcm/send/test123", IPAddress.Parse(PushEndpointTests.PublicTestIp), "", "", "high", Cancellation);

        Assert.Empty(resolver.Lookups);
        Assert.Equal([new IPEndPoint(IPAddress.Parse(PushEndpointTests.PublicTestIp), 443)], server.Dialled);
        var request = Assert.Single(server.Requests);
        Assert.Null(request.Header("Content-Encoding"));
        Assert.Empty(request.Body);
    }

    [Fact]
    public async Task A_private_endpoint_ip_is_refused_at_connect()
    {
        using var client = Client(FakeResolver.Of(PushEndpointTests.PublicTestIp));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PayloadSendAsync("m", ReferenceVector.Endpoint, IPAddress.Loopback, ReferenceVector.P256dh, ReferenceVector.Auth, "high", Cancellation));

        Assert.IsType<PrivateNetworkViolationException>(error.InnerException);
        Assert.Empty(server.Dialled);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();
}
