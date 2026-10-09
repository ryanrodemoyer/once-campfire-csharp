using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Campfire.Jobs.OpenGraph;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Vectors;

namespace Campfire.Web.Tests.Controllers.UnfurlLinks;

sealed class ScriptedResolver : IResolver
{
    readonly Dictionary<string, List<IPAddress>> hosts = new(StringComparer.OrdinalIgnoreCase);

    public ScriptedResolver()
    {
        Add("localhost", "127.0.0.1", "::1");
    }

    public IPAddress DefaultPublicAddress { get; set; } = IPAddress.Parse("93.184.216.34");

    public List<string> Lookups { get; } = [];

    public void Add(string host, params string[] ips)
    {
        hosts[host] = [.. ips.Select(IPAddress.Parse)];
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        lock (Lookups)
        {
            Lookups.Add(host);
        }

        if (hosts.TryGetValue(host, out var ips))
        {
            if (ips.Count == 0)
            {
                return Task.FromException<IReadOnlyList<IPAddress>>(new SocketException((int)SocketError.HostNotFound));
            }
            return Task.FromResult<IReadOnlyList<IPAddress>>(ips);
        }

        return Task.FromResult<IReadOnlyList<IPAddress>>([DefaultPublicAddress]);
    }
}

sealed class UnfurlServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stopping = new();
    readonly X509Certificate2 certificate = CreateCertificate();
    readonly List<OpenGraphRoute> routes = [];
    readonly Task accepting;
    readonly List<string?[]> requests = [];

    public UnfurlServer(IEnumerable<OpenGraphRoute>? initialRoutes = null)
    {
        if (initialRoutes is not null)
        {
            routes.AddRange(initialRoutes);
        }
        listener.Start();
        accepting = AcceptAsync();
    }

    public IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndpoint;

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

    public void AddRoute(OpenGraphRoute route) => routes.Add(route);

    public void AddHtml(string host, string path, string html, string method = "GET", int status = 200) =>
        routes.Add(new OpenGraphRoute(method, host, path, status, [["Content-Type", "text/html"]], html));

    public void AddImage(string host, string path, string contentType = "image/png", string method = "HEAD", int status = 200) =>
        routes.Add(new OpenGraphRoute(method, host, path, status, [["Content-Type", contentType]], ""));

    public void AddRedirect(string host, string path, string location, int status = 302) =>
        routes.Add(new OpenGraphRoute("GET", host, path, status, [["Location", location]], ""));

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(EndPoint, cancellationToken).ConfigureAwait(false);
        return client.GetStream();
    }

    public OpenGraphFetch CreateFetch(ScriptedResolver resolver)
    {
        var guard = new PrivateNetworkGuard(resolver);
        var fetch = new OpenGraphFetch(guard, (_, cancellationToken) => ConnectAsync(cancellationToken));
        AllowCertificate(fetch, certificate);
        return fetch;
    }

    static void AllowCertificate(OpenGraphFetch fetch, X509Certificate2 certificate)
    {
        var invoker = typeof(OpenGraphFetch).GetField("http", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(fetch);
        var handler = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(invoker) as SocketsHttpHandler;
        if (handler is not null)
        {
            var expectedThumbprint = certificate.GetCertHashString();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHashString() == expectedThumbprint;
        }
    }

    async Task AcceptAsync()
    {
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                connections.Add(ServeAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or InvalidOperationException)
        {
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    async Task ServeAsync(TcpClient connection)
    {
        using var _ = connection;
        try
        {
            var networkStream = connection.GetStream();
            var firstByte = new byte[1];
            if (await networkStream.ReadAsync(firstByte, stopping.Token).ConfigureAwait(false) != 1)
            {
                return;
            }

            Stream stream;
            if (firstByte[0] == 0x16) // TLS Handshake record
            {
                var prefixStream = new PrefixStream(firstByte, networkStream);
                var sslStream = new SslStream(prefixStream);
                await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                }, stopping.Token).ConfigureAwait(false);
                stream = sslStream;
            }
            else
            {
                stream = new PrefixStream(firstByte, networkStream);
            }

            var head = await ReadHeadAsync(stream).ConfigureAwait(false);
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
            await RespondAsync(stream, method, route).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
        }
    }

    async Task RespondAsync(Stream stream, string method, OpenGraphRoute route)
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
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), stopping.Token).ConfigureAwait(false);
        if (method == "HEAD")
        {
            return;
        }
        if (route.Chunked != true)
        {
            await stream.WriteAsync(body, stopping.Token).ConfigureAwait(false);
            return;
        }
        foreach (var chunk in body.Chunk(64 * 1024))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n"), stopping.Token).ConfigureAwait(false);
            await stream.WriteAsync(chunk, stopping.Token).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), stopping.Token).ConfigureAwait(false);
        }
        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), stopping.Token).ConfigureAwait(false);
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

    async Task<List<string>> ReadHeadAsync(Stream stream)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, stopping.Token).ConfigureAwait(false) == 1)
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

    static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=unfurl-test", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("www.example.com");
        names.AddDnsName("example.com");
        names.AddDnsName("twitter.com");
        names.AddDnsName("x.com");
        names.AddDnsName("fxtwitter.com");
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        await accepting.ConfigureAwait(false);
        certificate.Dispose();
        stopping.Dispose();
    }

    sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
    {
        int prefixRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (prefixRead < prefix.Length && count > 0)
            {
                buffer[offset] = prefix[prefixRead++];
                return 1;
            }
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (prefixRead < prefix.Length && buffer.Length > 0)
            {
                buffer.Span[0] = prefix[prefixRead++];
                return 1;
            }
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    }
}
