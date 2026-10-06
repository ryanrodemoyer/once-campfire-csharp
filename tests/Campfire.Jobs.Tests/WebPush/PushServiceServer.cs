using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Campfire.Jobs.RestrictedHttp;

namespace Campfire.Jobs.Tests.WebPush;

/// <summary>
/// A loopback push service speaking HTTPS as <c>fcm.googleapis.com</c> with a self-signed
/// certificate. It records each request and answers with <see cref="Status"/>.
/// </summary>
sealed class PushServiceServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stopping = new();
    readonly X509Certificate2 certificate = CreateCertificate("fcm.googleapis.com");
    readonly Task accepting;

    public PushServiceServer()
    {
        listener.Start();
        accepting = AcceptAsync();
    }

    public sealed record Request(string RequestLine, List<(string Name, string Value)> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>The status line's code and reason, e.g. "201 Created".</summary>
    public string Status { get; set; } = "201 Created";

    public List<Request> Requests { get; } = [];

    /// <summary>The addresses the client dialled.</summary>
    public List<IPEndPoint> Dialled { get; } = [];

    /// <summary>
    /// The app's handler (<see cref="RestrictedHttpHandlers.WebPush"/>) with every dial sent here,
    /// trusting this server's certificate unless <paramref name="trusted"/> is false.
    /// </summary>
    public SocketsHttpHandler Handler(PrivateNetworkGuard guard, bool trusted = true)
    {
        var handler = RestrictedHttpHandlers.WebPush(guard, async (endpoint, cancellationToken) =>
        {
            lock (Dialled)
            {
                Dialled.Add(endpoint);
            }
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            return client.GetStream();
        });
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            trusted && presented is not null && presented.GetCertHashString() == certificate.GetCertHashString();
        return handler;
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
        // Stopped between two accepts: "Not listening".
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
            await using var tls = new SslStream(connection.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stopping.Token);
            var head = await ReadHeadAsync(tls);
            if (head.Length == 0)
            {
                return;
            }
            var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1).Select(line => line.Split(':', 2)).Select(pair => (pair[0], pair[1].Trim())).ToList();
            var length = int.Parse(headers.FirstOrDefault(h => h.Item1.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Item2 ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length];
            await tls.ReadExactlyAsync(body, stopping.Token);
            lock (Requests)
            {
                Requests.Add(new Request(lines[0], headers, body));
            }
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {Status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stopping.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Security.Authentication.AuthenticationException)
        {
        }
    }

    async Task<string> ReadHeadAsync(Stream stream)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer, stopping.Token) == 1)
        {
            head.Append((char)buffer[0]);
        }
        return head.ToString();
    }

    static X509Certificate2 CreateCertificate(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        await accepting;
        certificate.Dispose();
        stopping.Dispose();
    }
}
