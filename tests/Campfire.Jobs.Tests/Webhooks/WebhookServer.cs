using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Campfire.Vectors;

namespace Campfire.Jobs.Tests.Webhooks;

/// <summary>
/// The oracle's server (reference-tools/oracle/webhook.rb): answers a POST to <c>/&lt;name&gt;</c>
/// with that case's status, headers and body (gzipped, after a delay, as the case says), then
/// closes the connection. Records each request: its line, headers in wire order, and body.
/// </summary>
sealed class WebhookServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stopping = new();
    readonly IReadOnlyList<WebhookCase> cases;
    readonly Task accepting;
    readonly List<WebhookRequest> requests = [];

    public WebhookServer(IReadOnlyList<WebhookCase> cases)
    {
        this.cases = cases;
        listener.Start();
        accepting = AcceptAsync();
    }

    public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndpoint;

    public string Url(string path) => $"http://127.0.0.1:{EndPoint.Port}/{path}";

    public IReadOnlyList<WebhookRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
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
            var headers = head.Skip(1).Select(line => (IReadOnlyList<string>)line.Split(": ", 2)).ToList();
            var length = headers.Where(header => header[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(header => int.Parse(header[1], System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, stopping.Token);
            lock (requests)
            {
                requests.Add(new WebhookRequest(head[0], headers, Encoding.UTF8.GetString(body)));
            }

            var target = head[0].Split(' ')[1];
            var reply = cases.Single(c => $"/{c.Name}" == target);
            if (reply.Delay is { } delay)
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), stopping.Token);
            }
            var content = reply.BodyB64 is { } b64 ? Convert.FromBase64String(b64) : Encoding.UTF8.GetBytes(reply.Body ?? "");
            if (reply.Gzip == true)
            {
                using var gzipped = new MemoryStream();
                using (var gzip = new GZipStream(gzipped, CompressionMode.Compress))
                {
                    gzip.Write(content);
                }
                content = gzipped.ToArray();
            }
            var response = new StringBuilder($"HTTP/1.1 {reply.Status} Status\r\n");
            foreach (var header in reply.Headers)
            {
                response.Append(header[0]).Append(": ").Append(header[1]).Append("\r\n");
            }
            if (reply.Gzip == true)
            {
                response.Append("Content-Encoding: gzip\r\n");
            }
            response.Append($"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), stopping.Token);
            await stream.WriteAsync(content, stopping.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
        }
    }

    static async Task<List<string>> ReadHeadAsync(NetworkStream stream)
    {
        var lines = new List<string>();
        var line = new List<byte>();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer) == 1)
        {
            if (buffer[0] == '\n')
            {
                var text = Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
                if (text.Length == 0)
                {
                    break;
                }
                lines.Add(text);
                line.Clear();
            }
            else
            {
                line.Add(buffer[0]);
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
