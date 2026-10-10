using System.Text;
using Campfire.Jobs.WebPush;

namespace Campfire.Web.Tests.Upstream.U06;

public sealed class WebPushConnectionsTests : IDisposable
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    readonly WebPushConnections connections = new();

    public void Dispose() => connections.Shutdown();

    static WebPushConnections.HTTP PinnedConnection(TestPushServer server, string ip = TestPushServer.Ip) =>
        new(TestPushServer.Host, server.Port)
        {
            IpAddress = ip,
            UseSsl = true,
            CertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.Subject.Contains($"CN={TestPushServer.Host}")
        };

    static WebPushConnections.PushRequest PushRequest(string path = "/push") =>
        new("POST", path, [("Content-Length", "7")], Encoding.UTF8.GetBytes("payload"));

    [Fact]
    public async Task A_delivery_to_the_same_host_and_address_reuses_the_open_connection()
    {
        await using var server = new TestPushServer();
        for (var i = 0; i < 2; i++)
        {
            var response = await connections.RequestAsync(PinnedConnection(server), PushRequest($"/push/{i}"), Cancellation);
            Assert.Equal(201, response.StatusCode);
        }

        Assert.Equal(1, server.Connections);
        Assert.Equal(["/push/0", "/push/1"], server.Requests);
    }

    [Fact]
    public async Task A_delivery_to_another_address_never_reuses_a_connection()
    {
        await using var server = new TestPushServer();
        await connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation);

        // Connect to 127.0.0.2 (different address)
        var other = PinnedConnection(server, "127.0.0.2");
        await connections.RequestAsync(other, PushRequest("/push/other"), Cancellation);

        Assert.Equal(2, server.Connections);
        Assert.Equal(["/push", "/push/other"], server.Requests);
    }

    [Theory]
    [InlineData(TestPushServer.HangUpMode.Fin)]
    [InlineData(TestPushServer.HangUpMode.CloseNotify)]
    public async Task A_connection_the_push_service_closed_while_idle_is_replaced_and_the_push_sent_once(TestPushServer.HangUpMode hangUp)
    {
        await using var server = new TestPushServer { HangUpAfterResponse = hangUp };
        var resp1 = await connections.RequestAsync(PinnedConnection(server), PushRequest("/push/0"), Cancellation);
        Assert.Equal(201, resp1.StatusCode);
        Assert.True(await server.HungUpAsync(TimeSpan.FromSeconds(5)));

        var resp2 = await connections.RequestAsync(PinnedConnection(server), PushRequest("/push/1"), Cancellation);
        Assert.Equal(201, resp2.StatusCode);
        Assert.Equal(2, server.Connections);
        Assert.Equal(["/push/0", "/push/1"], server.Requests);
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_refused_and_nothing_is_sent()
    {
        await using var server = new TestPushServer { CertificateForConnection = _ => "other.test" };
        await Assert.ThrowsAsync<WebPushOpenSslException>(() => connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_refused_when_a_dead_connection_is_replaced_too()
    {
        await using var server = new TestPushServer
        {
            HangUpAfterResponse = TestPushServer.HangUpMode.Fin,
            CertificateForConnection = conn => conn == 1 ? TestPushServer.Host : "other.test"
        };

        await connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation);
        Assert.True(await server.HungUpAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<WebPushOpenSslException>(() => connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_push_the_service_may_have_received_is_not_sent_again()
    {
        await using var server = new TestPushServer
        {
            DropRequest = number => number == 2
        };

        await connections.RequestAsync(PinnedConnection(server), PushRequest("/push/0"), Cancellation);

        var error = await Assert.ThrowsAsync<WebPushConnections.ConnectionLostException>(() =>
            connections.RequestAsync(PinnedConnection(server), PushRequest("/push/1"), Cancellation));

        Assert.Equal(["/push/0", "/push/1"], server.Requests);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task A_push_the_service_may_have_received_on_a_new_connection_is_not_reported_as_a_tls_failure_either()
    {
        await using var server = new TestPushServer
        {
            DropRequest = number => number == 1
        };

        var error = await Assert.ThrowsAsync<WebPushConnections.ConnectionLostException>(() =>
            connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation));

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_certificate_for_another_name_when_reconnecting_a_cleanly_closed_connection_is_reported()
    {
        await using var server = new TestPushServer
        {
            HangUpAfterResponse = TestPushServer.HangUpMode.CloseNotify,
            CertificateForConnection = conn => conn == 1 ? TestPushServer.Host : "other.test"
        };

        await connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation);
        Assert.True(await server.HungUpAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<WebPushOpenSslException>(() => connections.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation));
        Assert.Single(server.Requests);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task Only_new_direct_tls_connections_pinned_to_an_address_and_opened_as_http_are_pooled()
    {
        await using var server = new TestPushServer();
        var unpinned = new WebPushConnections.HTTP(TestPushServer.Host, server.Port, null) { UseSsl = true };
        var proxied = new WebPushConnections.HTTP(TestPushServer.Host, server.Port, "http://proxy.internal:3128") { IpAddress = TestPushServer.Ip, UseSsl = true };
        var plain = PinnedConnection(server);
        plain.UseSsl = false;
        var started = PinnedConnection(server);
        started.Start();
        var untracked = new WebPushConnections.NetHttp(TestPushServer.Host, server.Port, null) { IpAddress = TestPushServer.Ip, UseSsl = true };

        WebPushConnections.NetHttp[] badConnections = [unpinned, proxied, plain, started, untracked];
        foreach (var http in badConnections)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => connections.RequestAsync(http, PushRequest(), Cancellation));
            Assert.Equal("Only new, direct TLS connections pinned to an address and opened as WebPush::Connections::HTTP are pooled", ex.Message);
        }

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_idle_pool_closes_connections_beyond_its_bound()
    {
        var bounded = new WebPushConnections(maxIdle: 1);
        try
        {
            await using var server = new TestPushServer();
            await using var other = new TestPushServer();

            await bounded.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation);
            await bounded.RequestAsync(PinnedConnection(other), PushRequest(), Cancellation);

            // The second connection exceeded maxIdle=1 so it was closed immediately upon checkin
            Assert.True(await other.HungUpAsync(TimeSpan.FromSeconds(5)));
            Assert.False(await server.HungUpAsync(TimeSpan.FromMilliseconds(100)));

            await bounded.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            bounded.Shutdown();
        }
    }

    [Fact]
    public async Task Idle_connections_are_closed_after_the_keep_alive_timeout_and_on_shutdown()
    {
        var quickTimeout = new WebPushConnections(keepAliveTimeout: 0);
        try
        {
            await using var server = new TestPushServer();
            await using var other = new TestPushServer();

            await quickTimeout.RequestAsync(PinnedConnection(server), PushRequest(), Cancellation);
            await quickTimeout.RequestAsync(PinnedConnection(other), PushRequest(), Cancellation);

            // keepAliveTimeout is 0 so checking in or checkout closes expired idle connections
            Assert.True(await server.HungUpAsync(TimeSpan.FromSeconds(5)));

            quickTimeout.Shutdown();
            Assert.True(await other.HungUpAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            quickTimeout.Shutdown();
        }
    }
}
