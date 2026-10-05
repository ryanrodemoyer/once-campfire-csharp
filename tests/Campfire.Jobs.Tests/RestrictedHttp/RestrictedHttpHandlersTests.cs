using System.Net;
using System.Net.Sockets;
using System.Text;
using Campfire.Jobs.RestrictedHttp;

namespace Campfire.Jobs.Tests.RestrictedHttp;

public sealed class RestrictedHttpHandlersTests : IAsyncDisposable
{
    static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");

    readonly LocalServer server = new();
    readonly List<IPEndPoint> dialed = [];

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // Every dial reaches the local server, whatever address the handler chose; the test asserts
    // on the address it chose.
    ValueTask<Stream> Dial(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        dialed.Add(endpoint);
        return server.ConnectAsync(cancellationToken);
    }

    HttpClient OpenGraph(IResolver resolver) => new(RestrictedHttpHandlers.OpenGraph(new PrivateNetworkGuard(resolver), Dial));

    [Fact]
    public async Task ConnectsToTheAddressTheGuardAdmits()
    {
        var resolver = FakeResolver.Of("127.0.0.1", "93.184.216.34");
        using var client = OpenGraph(resolver);

        var body = await client.GetStringAsync(new Uri("http://example.com/page"), Cancellation);

        Assert.Equal("ok", body);
        Assert.Equal([new IPEndPoint(Public, 80)], dialed);
        Assert.Equal(["example.com"], resolver.Lookups);
        Assert.Equal("example.com", server.Hosts.Single());
    }

    [Theory]
    [InlineData("http://93.184.216.34:8080/", "93.184.216.34:8080")]
    [InlineData("http://[2606:4700:4700::1111]/", "[2606:4700:4700::1111]:80")]
    public async Task ConnectsToPublicLiteralsWithoutDns(string url, string endpoint)
    {
        var resolver = FakeResolver.Of("127.0.0.1");
        using var client = OpenGraph(resolver);

        Assert.Equal("ok", await client.GetStringAsync(new Uri(url), Cancellation));
        Assert.Equal([IPEndPoint.Parse(endpoint)], dialed);
        Assert.Empty(resolver.Lookups);
    }

    [Theory]
    [InlineData("http://private.example.com/", "10.0.0.5")]
    [InlineData("http://imds.example.com/", "169.254.169.254")]
    [InlineData("http://mapped.example.com/", "::ffff:127.0.0.1")]
    [InlineData("http://ula.example.com/", "fd00:ec2::254")]
    [InlineData("http://127.0.0.1/", "93.184.216.34")]
    [InlineData("http://localhost/", "127.0.0.1")]
    [InlineData("http://[::1]/", "93.184.216.34")]
    [InlineData("http://[fe80::1]/", "93.184.216.34")]
    [InlineData("http://[::ffff:169.254.169.254]/", "93.184.216.34")]
    [InlineData("http://[::ffff:0:169.254.169.254]/", "93.184.216.34")]
    [InlineData("http://2130706433/", "93.184.216.34")]
    [InlineData("http://0x7f000001/", "93.184.216.34")]
    [InlineData("http://0177.0.0.1:8080/", "93.184.216.34")]
    public async Task RefusesPrivateLoopbackLinkLocalAndMappedTargets(string url, string answer)
    {
        using var client = OpenGraph(FakeResolver.Of(answer));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(url), Cancellation));

        Assert.IsType<PrivateNetworkViolationException>(error.InnerException);
        Assert.Empty(dialed);
    }

    [Fact]
    public async Task PinnedRequestIgnoresALaterDnsAnswer()
    {
        // DNS rebinding: the first answer is public, every later one is loopback.
        var resolver = FakeResolver.Sequence(["93.184.216.34"], ["127.0.0.1"]);
        var pinned = await new PrivateNetworkGuard(resolver).ResolveAsync("rebind.example.com", Cancellation);
        using var client = OpenGraph(resolver);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://rebind.example.com/"));
        request.Options.Set(RestrictedHttpHandlers.PinnedAddress, pinned);

        using var response = await client.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new IPEndPoint(Public, 80)], dialed);
        Assert.Single(resolver.Lookups);
    }

    [Fact]
    public async Task EveryRequestIsCheckedAgainSoARebindIsRefused()
    {
        var resolver = FakeResolver.Sequence(["93.184.216.34"], ["127.0.0.1"]);
        using var client = OpenGraph(resolver);

        Assert.Equal("ok", await client.GetStringAsync(new Uri("http://rebind.example.com/"), Cancellation));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://rebind.example.com/"), Cancellation));

        Assert.IsType<PrivateNetworkViolationException>(error.InnerException);
        Assert.Equal([new IPEndPoint(Public, 80)], dialed);
        Assert.Equal(2, resolver.Lookups.Count);
    }

    [Fact]
    public async Task RefusesAPinnedPrivateAddress()
    {
        using var client = OpenGraph(FakeResolver.Of("93.184.216.34"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://example.com/"));
        request.Options.Set(RestrictedHttpHandlers.PinnedAddress, IPAddress.Parse("::ffff:169.254.169.254"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, Cancellation));

        Assert.IsType<PrivateNetworkViolationException>(error.InnerException);
        Assert.Empty(dialed);
    }

    [Fact]
    public async Task UnresolvableHostIsNotAViolation()
    {
        using var client = OpenGraph(FakeResolver.Of());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://nxdomain.example.com/"), Cancellation));

        Assert.IsType<UnresolvableHostException>(error.InnerException);
    }

    [Fact]
    public async Task DoesNotFollowRedirects()
    {
        server.Response = "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/\r\nContent-Length: 0\r\n\r\n";
        using var client = OpenGraph(FakeResolver.Of("93.184.216.34"));

        using var response = await client.GetAsync(new Uri("http://example.com/"), Cancellation);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(dialed);
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/send")]
    [InlineData("https://fcm.googleapis.com:8443/send")]
    public async Task WebPushOnlySendsToHttpsOn443(string url)
    {
        var resolver = FakeResolver.Of("93.184.216.34");
        using var client = new HttpClient(RestrictedHttpHandlers.WebPush(new PrivateNetworkGuard(resolver), Dial));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(new Uri(url), new StringContent(""), Cancellation));

        Assert.IsType<PrivateNetworkViolationException>(error.InnerException);
        Assert.Empty(resolver.Lookups);
        Assert.Empty(dialed);
    }

    [Fact]
    public async Task WebPushConnectsToThePinnedAddress()
    {
        var resolver = FakeResolver.Of("127.0.0.1");
        // Stop at the dial: the local server doesn't speak TLS.
        using var client = new HttpClient(RestrictedHttpHandlers.WebPush(new PrivateNetworkGuard(resolver), (endpoint, _) =>
        {
            dialed.Add(endpoint);
            throw new IOException("dialed");
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://fcm.googleapis.com/send"));
        request.Options.Set(RestrictedHttpHandlers.PinnedAddress, Public);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, Cancellation));

        Assert.Equal([new IPEndPoint(Public, 443)], dialed);
        Assert.Empty(resolver.Lookups);
    }

    [Fact]
    public async Task WebhooksReachInternalServices()
    {
        using var client = new HttpClient(RestrictedHttpHandlers.Webhook());

        var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{server.Port}/bot"), Cancellation);

        Assert.Equal("ok", body);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    /// <summary>A loopback HTTP/1.1 server that answers every request with <see cref="Response"/>.</summary>
    sealed class LocalServer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stopping = new();
        readonly Task accepting;

        public LocalServer()
        {
            listener.Start();
            accepting = AcceptAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public string Response { get; set; } = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 2\r\n\r\nok";

        public List<string> Hosts { get; } = [];

        public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, cancellationToken);
            return client.GetStream();
        }

        async Task AcceptAsync()
        {
            var connections = new List<Task>();
            try
            {
                while (true)
                {
                    connections.Add(ServeAsync(await listener.AcceptTcpClientAsync(stopping.Token)));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException)
            {
            }
            await Task.WhenAll(connections);
        }

        // Keeps the connection open between requests, so a client that reused it could.
        async Task ServeAsync(TcpClient connection)
        {
            using var _ = connection;
            try
            {
                var stream = connection.GetStream();
                while (await ReadHeadAsync(stream) is { Length: > 0 } head)
                {
                    var host = head.Split("\r\n").FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
                    if (host is not null)
                    {
                        Hosts.Add(host[5..].Trim());
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(Response), stopping.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
            }
        }

        async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer, stopping.Token) == 1)
            {
                head.Append((char)buffer[0]);
            }
            return head.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            await accepting;
            stopping.Dispose();
        }
    }
}
