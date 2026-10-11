using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Campfire.Jobs.OpenGraph;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Web.Tests.Controllers.Messages;
using Campfire.Web.Tests.Controllers.UnfurlLinks;

namespace Campfire.Web.Tests.Upstream.U06;

public sealed class UnfurlHardeningTests : IAsyncDisposable
{
    const string david = "davidSessionToken0000001";
    const long davidId = 127326141;

    readonly MessagesApp app;
    readonly ScriptedResolver resolver;

    public UnfurlHardeningTests()
    {
        app = new MessagesApp(
            new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
            [
                "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                    $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            ]);
        resolver = new ScriptedResolver();
    }

    [Fact]
    public async Task Create_gives_up_on_a_host_that_accepts_the_connection_and_never_answers()
    {
        var (server, port, connectionCount) = StartServer(_ =>
        {
            // Accept the connection and never answer (keep socket open until cancelled/test finishes)
            Thread.Sleep(10000);
        });
        await using var _ = server;

        resolver.Add("www.example.com", "93.184.216.34");
        var guard = new PrivateNetworkGuard(resolver);
        using var fetch = new OpenGraphFetch(guard, async (_, ct) =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return client.GetStream();
        })
        {
            Timeout = TimeSpan.FromSeconds(0.2)
        };
        app.App.OpenGraphFetch = fetch;

        var sw = Stopwatch.StartNew();
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            $"url=http%3A%2F%2Fwww.example.com%3A{port}%2F");
        sw.Stop();

        Assert.Equal(204, response.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Took {sw.Elapsed.TotalSeconds}s, expected < 2.0s");
        Assert.Equal(1, connectionCount());
    }

    [Fact]
    public async Task Create_gives_up_on_headers_that_trickle_in_past_the_deadline()
    {
        var (server, port, _) = StartServer(stream =>
        {
            var initial = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Padding: ");
            stream.Write(initial);
            for (var i = 0; i < 50; i++)
            {
                stream.WriteByte((byte)'x');
                stream.Flush();
                Thread.Sleep(50);
            }
        });
        await using var _ = server;

        resolver.Add("www.example.com", "93.184.216.34");
        var guard = new PrivateNetworkGuard(resolver);
        using var fetch = new OpenGraphFetch(guard, async (_, ct) =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return client.GetStream();
        });
        app.App.OpenGraphFetch = fetch;
        app.App.UnfurlDeadline = TimeSpan.FromSeconds(0.5);

        var sw = Stopwatch.StartNew();
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            $"url=http%3A%2F%2Fwww.example.com%3A{port}%2F");
        sw.Stop();

        Assert.Equal(204, response.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Took {sw.Elapsed.TotalSeconds}s, expected < 2.0s");
    }

    [Fact]
    public async Task Create_gives_up_on_a_body_that_trickles_in_past_the_deadline()
    {
        var (server, port, _) = StartServer(stream =>
        {
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100\r\n\r\n");
            stream.Write(headers);
            for (var i = 0; i < 50; i++)
            {
                stream.WriteByte((byte)'x');
                stream.Flush();
                Thread.Sleep(50);
            }
        });
        await using var _ = server;

        resolver.Add("www.example.com", "93.184.216.34");
        var guard = new PrivateNetworkGuard(resolver);
        using var fetch = new OpenGraphFetch(guard, async (_, ct) =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return client.GetStream();
        });
        app.App.OpenGraphFetch = fetch;
        app.App.UnfurlDeadline = TimeSpan.FromSeconds(0.5);

        var sw = Stopwatch.StartNew();
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            $"url=http%3A%2F%2Fwww.example.com%3A{port}%2F");
        sw.Stop();

        Assert.Equal(204, response.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Took {sw.Elapsed.TotalSeconds}s, expected < 2.0s");
    }

    [Fact]
    public async Task Create_gives_up_on_redirects_that_keep_coming_past_the_deadline()
    {
        var (server, port, _) = StartServer(stream =>
        {
            Thread.Sleep(200);
            var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://www.example.com/\r\nContent-Length: 0\r\n\r\n");
            stream.Write(redirect);
        });
        await using var _ = server;

        resolver.Add("www.example.com", "93.184.216.34");
        var guard = new PrivateNetworkGuard(resolver);
        using var fetch = new OpenGraphFetch(guard, async (_, ct) =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return client.GetStream();
        });
        app.App.OpenGraphFetch = fetch;
        app.App.UnfurlDeadline = TimeSpan.FromSeconds(0.5);

        var sw = Stopwatch.StartNew();
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            $"url=http%3A%2F%2Fwww.example.com%3A{port}%2F");
        sw.Stop();

        Assert.Equal(204, response.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Took {sw.Elapsed.TotalSeconds}s, expected < 2.0s");
    }

    [Fact]
    public async Task Create_gives_up_on_a_host_name_that_takes_too_long_to_look_up()
    {
        var sleepingResolver = new SlowResolver(TimeSpan.FromSeconds(2));
        var guard = new PrivateNetworkGuard(sleepingResolver);
        using var fetch = new OpenGraphFetch(guard);
        app.App.OpenGraphFetch = fetch;
        app.App.UnfurlDeadline = TimeSpan.FromSeconds(0.5);

        var sw = Stopwatch.StartNew();
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=http%3A%2F%2Fslow.example.com%2F");
        sw.Stop();

        Assert.Equal(204, response.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"Took {sw.Elapsed.TotalSeconds}s, expected < 2.0s");
    }

    [Fact]
    public async Task Fetch_document_connects_to_the_resolved_ip_even_when_a_proxy_is_configured()
    {
        var saved = new Dictionary<string, string?>();
        string[] proxyKeys = ["http_proxy", "HTTP_PROXY"];
        foreach (var key in proxyKeys)
        {
            saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, "http://proxy.internal:3128");
        }

        try
        {
            var dialed = new List<IPEndPoint>();
            var guard = new PrivateNetworkGuard(resolver);
            using var fetch = new OpenGraphFetch(guard, (endpoint, ct) =>
            {
                dialed.Add(endpoint);
                throw new InvalidOperationException("not_proxied");
            });

            var uri = Campfire.RichText.Sanitize.RubyUri.Parse("http://www.example.com/");
            var ex = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
                fetch.FetchDocumentAsync(uri, IPAddress.Parse("1.2.3.4"), TestContext.Current.CancellationToken));

            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Equal("not_proxied", ex.InnerException.Message);
            var endpoint = Assert.Single(dialed);
            Assert.Equal(IPAddress.Parse("1.2.3.4"), endpoint.Address);
            Assert.Equal(80, endpoint.Port);
        }
        finally
        {
            foreach (var key in proxyKeys)
            {
                Environment.SetEnvironmentVariable(key, saved[key]);
            }
        }
    }

    static (IAsyncDisposable Server, int Port, Func<int> Connections) StartServer(Action<NetworkStream> handle)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stopping = new CancellationTokenSource();
        var connections = 0;

        var acceptTask = Task.Run(async () =>
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    Interlocked.Increment(ref connections);
                    _ = Task.Run(() =>
                    {
                        using var clientSession = client;
                        try
                        {
                            var stream = clientSession.GetStream();
                            // Read request line
                            var buffer = new byte[1024];
                            _ = stream.Read(buffer.AsSpan());
                            handle(stream);
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
            }
        });

        var disposable = new ServerDisposable(listener, stopping, acceptTask);
        return (disposable, port, () => Volatile.Read(ref connections));
    }

    sealed class ServerDisposable(TcpListener listener, CancellationTokenSource stopping, Task acceptTask) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            try { await acceptTask; } catch { }
            stopping.Dispose();
        }
    }

    sealed class SlowResolver(TimeSpan delay) : IResolver
    {
        public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return [IPAddress.Parse("93.184.216.34")];
        }
    }

    public ValueTask DisposeAsync()
    {
        app.Dispose();
        return ValueTask.CompletedTask;
    }
}
