using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Campfire.Cable.Server;
using Campfire.RailsCompat.Crypto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Campfire.Cable.Tests.Server;

public static class CableTestApp
{
    public static async Task<CableTestApp<TUser>> StartAsync<TUser>(CableServer<TUser> server)
        where TUser : class
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        app.Map(CableProtocol.DefaultMountPath, (HttpContext context) => server.HandleAsync(context));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Urls.First();
        return new CableTestApp<TUser>(app, server, new Uri(address));
    }
}

/// <summary>A cable server mounted at /cable on Kestrel, on a free local port.</summary>
public sealed class CableTestApp<TUser> : IAsyncDisposable
    where TUser : class
{
    readonly WebApplication app;

    internal CableTestApp(WebApplication app, CableServer<TUser> server, Uri baseUri)
    {
        this.app = app;
        Server = server;
        Origin = baseUri.GetLeftPart(UriPartial.Authority);
        Url = new Uri(Origin.Replace("http://", "ws://", StringComparison.Ordinal) + "/cable");
    }

    public CableServer<TUser> Server { get; }

    public Uri Url { get; }

    /// <summary><c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public string Origin { get; }

    /// <summary>Opens a socket offering both Action Cable protocols, as the browser client does.</summary>
    public async Task<CableClient> ConnectAsync(string? cookie, string? origin = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("actioncable-v1-json");
        socket.Options.AddSubProtocol("actioncable-unsupported");
        socket.Options.SetRequestHeader("Origin", origin ?? Origin);
        socket.Options.CollectHttpResponseDetails = true;
        if (cookie is not null)
        {
            socket.Options.SetRequestHeader("Cookie", cookie);
        }
        await socket.ConnectAsync(Url, TestContext.Current.CancellationToken);
        return new CableClient(socket);
    }

    /// <summary>
    /// A raw HTTP/1.1 GET of /cable with <paramref name="headers"/>, on a connection the client
    /// closes after the response (<c>Connection: close</c> unless the headers name one).
    /// </summary>
    public async Task<(int Status, string ContentType, string Body)> RawRequestAsync(params string[] headers)
    {
        var uri = new Uri(Origin);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, TestContext.Current.CancellationToken);
        await using var stream = tcp.GetStream();
        var request = new StringBuilder($"GET /cable HTTP/1.1\r\nHost: {uri.Authority}\r\n");
        foreach (var header in headers)
        {
            request.Append(header).Append("\r\n");
        }
        if (!headers.Any(h => h.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)))
        {
            request.Append("Connection: close\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = response[..split];
        var body = response[(split + 4)..];
        if (head.Contains("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = Dechunk(body);
        }
        var status = int.Parse(head.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var contentType = head.Split("\r\n").Select(line => line.Split(':', 2)).FirstOrDefault(parts => parts[0].Equals("content-type", StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";
        return (status, contentType, body);
    }

    /// <summary>A WebSocket upgrade request with the given <c>Origin</c>, read as plain HTTP.</summary>
    public Task<(int Status, string ContentType, string Body)> UpgradeRequestAsync(string origin, string? cookie) =>
        RawRequestAsync([
            "Connection: Upgrade, close",
            "Upgrade: websocket",
            "Sec-WebSocket-Version: 13",
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==",
            "Sec-WebSocket-Protocol: actioncable-v1-json, actioncable-unsupported",
            $"Origin: {origin}",
            .. cookie is null ? Array.Empty<string>() : [$"Cookie: {cookie}"],
        ]);

    static string Dechunk(string body)
    {
        var result = new StringBuilder();
        var rest = body;
        while (rest.IndexOf("\r\n", StringComparison.Ordinal) is var at and >= 0)
        {
            var size = Convert.ToInt32(rest[..at].Trim(), 16);
            if (size == 0)
            {
                break;
            }
            result.Append(rest.AsSpan(at + 2, size));
            rest = rest[(at + 2 + size + 2)..];
        }
        return result.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        Server.Dispose();
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
    }
}

/// <summary>
/// A WebSocket client that reads in the background, so waiting for "nothing more" never cancels a
/// receive (which would abort the socket). Events are text frames, <c>close Some((code, "reason"))</c>
/// when the server closes (answered, as browsers do), and <c>end</c> when the socket is gone.
/// </summary>
public sealed class CableClient : IDisposable
{
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly System.Threading.Channels.Channel<string> events = System.Threading.Channels.Channel.CreateUnbounded<string>();
    readonly Task pump;

    public CableClient(ClientWebSocket socket)
    {
        Socket = socket;
        Protocol = socket.SubProtocol;
        pump = Task.Run(PumpAsync);
    }

    public ClientWebSocket Socket { get; }

    public string? Protocol { get; }

    public Task SendAsync(string text) =>
        Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

    public Task SendAsync(JsonNode command) => SendAsync(RailsJson.Generate(command));

    public Task SubscribeAsync(string identifier) => SendAsync(new JsonObject { ["command"] = "subscribe", ["identifier"] = identifier });

    public Task UnsubscribeAsync(string identifier) => SendAsync(new JsonObject { ["command"] = "unsubscribe", ["identifier"] = identifier });

    public Task PerformAsync(string identifier, JsonObject data) =>
        SendAsync(new JsonObject { ["command"] = "message", ["identifier"] = identifier, ["data"] = RailsJson.Generate(data) });

    /// <summary>The next event, skipping pings unless <paramref name="pings"/>.</summary>
    public async Task<string> NextAsync(bool pings = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Patience);
        while (true)
        {
            var next = await events.Reader.ReadAsync(timeout.Token);
            if (pings || !IsPing(next))
            {
                return next;
            }
        }
    }

    /// <summary>
    /// Events until the socket has been quiet for a moment, or, when awaiting a ping, until the
    /// first ping (normalized). Other pings are dropped: when they land is timing, not protocol.
    /// </summary>
    public async Task<List<string>> CollectAsync(bool awaitPing = false)
    {
        var frames = new List<string>();
        var quiet = awaitPing ? TimeSpan.FromSeconds(4) : Quiet;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(quiet);
            string next;
            try
            {
                next = await events.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                return frames;
            }
            catch (ChannelClosedException)
            {
                return frames;
            }
            if (IsPing(next))
            {
                if (awaitPing)
                {
                    var ping = JsonNode.Parse(next)!;
                    frames.Add(next.Replace(ping["message"]!.ToJsonString(), "<unix>", StringComparison.Ordinal));
                    return frames;
                }
                continue;
            }
            frames.Add(next);
        }
    }

    public async Task AssertSilentAsync() => Assert.Empty(await CollectAsync());

    static bool IsPing(string frame) => frame.StartsWith("{\"type\":\"ping\"", StringComparison.Ordinal);

    async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await Socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    events.Writer.TryWrite($"close Some(({(int?)result.CloseStatus}, \"{result.CloseStatusDescription}\"))");
                    if (Socket.State == WebSocketState.CloseReceived)
                    {
                        await Socket.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.Empty, null, CancellationToken.None);
                    }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    events.Writer.TryWrite(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                    message.SetLength(0);
                }
            }
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        events.Writer.TryWrite("end");
        events.Writer.TryComplete();
    }

    public void Dispose()
    {
        Socket.Abort();
        Socket.Dispose();
        _ = pump;
    }
}

/// <summary>An <see cref="ILogger"/> that keeps what it's told.</summary>
public sealed class TestLogger : ILogger
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}
