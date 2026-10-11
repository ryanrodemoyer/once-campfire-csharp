using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.WebPush;

namespace Campfire.Web.Tests.Upstream.U06;

public sealed class WebPushPersistentRequestTests
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    const string endpoint = "https://fcm.googleapis.com/fcm/send/test123";

    [Fact]
    public async Task Nothing_is_sent_without_a_resolved_endpoint_ip()
    {
        var guard = PrivateNetworkGuard.System;
        using var client = new WebPushClient(RestrictedHttpHandlers.WebPush(guard), guard,
            new VapidIdentification("publicKey", "privateKey"), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PayloadSendAsync("", endpoint, null, "", "", "high", Cancellation));

        Assert.Equal("Push deliveries must be pinned to a resolved endpoint IP", ex.Message);
    }

    [Fact]
    public async Task Deliveries_through_the_connections_pool_to_the_same_host_and_address_share_one_connection()
    {
        await using var server = new TestPushServer();
        using var connections = new WebPushConnections();

        var http = new WebPushConnections.HTTP(TestPushServer.Host, server.Port)
        {
            IpAddress = TestPushServer.Ip,
            UseSsl = true,
            CertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.Subject.Contains($"CN={TestPushServer.Host}")
        };

        var req1 = new WebPushConnections.PushRequest("POST", "/push/1", [("Content-Length", "0")], []);
        var req2 = new WebPushConnections.PushRequest("POST", "/push/2", [("Content-Length", "0")], []);

        var resp1 = await connections.RequestAsync(http, req1, Cancellation);
        var resp2 = await connections.RequestAsync(http, req2, Cancellation);

        Assert.Equal(201, resp1.StatusCode);
        Assert.Equal(201, resp2.StatusCode);
        Assert.Equal(1, server.Connections);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_pooled_delivery_checks_the_response_like_any_other()
    {
        await using var server = new TestPushServer { Status = "410 Gone" };
        using var connections = new WebPushConnections();

        var http = new WebPushConnections.HTTP(TestPushServer.Host, server.Port)
        {
            IpAddress = TestPushServer.Ip,
            UseSsl = true,
            CertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.Subject.Contains($"CN={TestPushServer.Host}")
        };

        var resp = await connections.RequestAsync(http, new WebPushConnections.PushRequest("POST", "/push", [("Content-Length", "0")], []), Cancellation);
        Assert.Equal(410, resp.StatusCode);
    }

    [Fact]
    public async Task Pooled_deliveries_are_pinned_and_ignore_proxy_env_too()
    {
        await using var server = new TestPushServer();
        using var connections = new WebPushConnections();

        var saved = new Dictionary<string, string?>();
        string[] proxyKeys = ["http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY"];
        foreach (var key in proxyKeys)
        {
            saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, "http://proxy.internal:3128");
        }

        try
        {
            var http = new WebPushConnections.HTTP(TestPushServer.Host, server.Port)
            {
                IpAddress = TestPushServer.Ip,
                UseSsl = true,
                CertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.Subject.Contains($"CN={TestPushServer.Host}")
            };

            var resp = await connections.RequestAsync(http, new WebPushConnections.PushRequest("POST", "/push/direct", [("Content-Length", "0")], []), Cancellation);
            Assert.Equal(201, resp.StatusCode);
            Assert.Equal(1, server.Connections);
            Assert.Equal(["/push/direct"], server.Requests);
        }
        finally
        {
            foreach (var key in proxyKeys)
            {
                Environment.SetEnvironmentVariable(key, saved[key]);
            }
        }
    }
}
