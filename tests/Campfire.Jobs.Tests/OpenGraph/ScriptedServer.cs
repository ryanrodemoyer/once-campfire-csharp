using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Campfire.Vectors;

namespace Campfire.Jobs.Tests.OpenGraph;

/// <summary>
/// The oracle's server (reference-tools oracle/opengraph.rb): answers each request with the
/// route matching its method, Host (without the port) and target, or a 404, then closes the
/// connection. Records every request as the oracle does.
/// </summary>
sealed class ScriptedServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stopping = new();
    readonly IReadOnlyList<OpenGraphRoute> routes;
    readonly Task accepting;
    readonly List<string?[]> requests = [];

    public ScriptedServer(IReadOnlyList<OpenGraphRoute> routes)
    {
        this.routes = routes;
        listener.Start();
        accepting = AcceptAsync();
    }

    public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndpoint;

    /// <summary>[method, Host, target, Accept, Accept-Encoding, User-Agent] for each request.</summary>
    public IReadOnlyList<string?[]> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(EndPoint, cancellationToken);
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
        catch (Exception e) when (e is OperationCanceledException or SocketException or InvalidOperationException)
        {
        }
        await Task.WhenAll(connections);
    }

    async Task ServeAsync(TcpClient connection)
    {
        using var _ = connection;
        try
        {
            var stream = connection.GetStream();
            var head = await ReadHeadAsync(stream);
            if (head.Count == 0)
            {
                return;
            }
            var requestLine = head[0].Split(' ');
            var (method, target) = (requestLine[0], requestLine[1]);
            var headers = head.Skip(1)
                .Select(line => line.Split(": ", 2))
                .GroupBy(pair => pair[0].ToLowerInvariant())
                .ToDictionary(group => group.Key, group => group.Last().ElementAtOrDefault(1));
            string? Header(string name) => headers.GetValueOrDefault(name);
            lock (requests)
            {
                requests.Add([method, Header("host"), target, Header("accept"), Header("accept-encoding"), Header("user-agent")]);
            }

            var host = (Header("host") ?? "").Split(':')[0];
            var route = routes.FirstOrDefault(r => r.Method == method && r.Host == host && r.Path == target)
                ?? new OpenGraphRoute("GET", host, target, 404, [["Content-Type", "text/plain"]], "not found");
            await RespondAsync(stream, method, route);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
        }
    }

    async Task RespondAsync(NetworkStream stream, string method, OpenGraphRoute route)
    {
        var body = Body(route);
        var head = new StringBuilder($"HTTP/1.1 {route.Status} Status\r\n");
        foreach (var header in route.Headers)
        {
            head.Append($"{header[0]}: {header[1]}\r\n");
        }
        if (route.Gzip == true)
        {
            head.Append("Content-Encoding: gzip\r\n");
        }
        if (route.Chunked == true)
        {
            head.Append("Transfer-Encoding: chunked\r\n");
        }
        else if (!route.Headers.Any(header => header[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
        {
            head.Append($"Content-Length: {body.Length}\r\n");
        }
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), stopping.Token);
        if (method == "HEAD")
        {
            return;
        }
        if (route.Chunked != true)
        {
            await stream.WriteAsync(body, stopping.Token);
            return;
        }
        foreach (var chunk in body.Chunk(64 * 1024))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n"), stopping.Token);
            await stream.WriteAsync(chunk, stopping.Token);
            await stream.WriteAsync("\r\n"u8.ToArray(), stopping.Token);
        }
        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), stopping.Token);
    }

    static byte[] Body(OpenGraphRoute route)
    {
        var body = route.BodyB64 is { } b64 ? Convert.FromBase64String(b64)
            : route.BodyRepeat is { } repeat ? Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(repeat[0].GetString(), repeat[1].GetInt32())))
            : Encoding.UTF8.GetBytes(route.Body ?? "");
        if (route.PadTo is { } padTo)
        {
            body = [.. body, .. Enumerable.Repeat((byte)' ', (int)padTo - body.Length)];
        }
        if (route.Gzip != true)
        {
            return body;
        }
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal))
        {
            gzip.Write(body);
        }
        return compressed.ToArray();
    }

    async Task<List<string>> ReadHeadAsync(NetworkStream stream)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, stopping.Token) == 1)
        {
            line.Append((char)buffer[0]);
            if (line.Length >= 2 && line[^2] == '\r' && line[^1] == '\n')
            {
                if (line.Length == 2)
                {
                    break;
                }
                lines.Add(line.ToString(0, line.Length - 2));
                line.Clear();
            }
        }
        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        await accepting;
        stopping.Dispose();
    }
}
