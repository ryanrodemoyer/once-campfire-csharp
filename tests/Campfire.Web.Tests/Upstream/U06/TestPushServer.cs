using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace Campfire.Web.Tests.Upstream.U06;

/// <summary>
/// A push service on 127.0.0.1 for delivery tests (port of reference PushServiceTestHelper):
/// TLS with certificates for HOST or other names, HTTP/1.1 keep-alive, hang-up triggers.
/// </summary>
public sealed class TestPushServer : IAsyncDisposable
{
    public const string Host = "push.test";
    public const string Ip = "127.0.0.1";

    readonly TcpListener listener = new(IPAddress.Any, 0);
    readonly CancellationTokenSource stopping = new();
    readonly Task accepting;
    readonly Channel<bool> closed = Channel.CreateUnbounded<bool>();
    readonly Lock gate = new();
    readonly List<string> requests = [];
    int connections;

    public enum HangUpMode
    {
        None,
        Fin,
        CloseNotify
    }

    public string Status { get; set; } = "201 Created";
    public HangUpMode HangUpAfterResponse { get; set; } = HangUpMode.None;
    public Func<int, bool>? DropRequest { get; set; }
    public Func<int, string>? CertificateForConnection { get; set; }

    public int Connections
    {
        get
        {
            lock (gate) return connections;
        }
    }

    public List<string> Requests
    {
        get
        {
            lock (gate) return [.. requests];
        }
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public TestPushServer()
    {
        listener.Start();
        accepting = AcceptAsync();
    }

    public async Task<bool> HungUpAsync(TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);
        try
        {
            return await closed.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    async Task AcceptAsync()
    {
        var active = new List<Task>();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                active.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        await Task.WhenAll(active).ConfigureAwait(false);
    }

    async Task ServeAsync(TcpClient client)
    {
        using var clientConnection = client;
        int connectionNumber;
        lock (gate)
        {
            connections++;
            connectionNumber = connections;
        }

        var certHost = CertificateForConnection?.Invoke(connectionNumber) ?? Host;
        using var cert = CreateCertificate(certHost);

        try
        {
            await using var tls = new SslStream(client.GetStream(), false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert
            }, stopping.Token).ConfigureAwait(false);

            while (!stopping.IsCancellationRequested)
            {
                var requestLine = await ReadLineAsync(tls, stopping.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine))
                {
                    break;
                }

                var contentLength = 0;
                while (true)
                {
                    var header = await ReadLineAsync(tls, stopping.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(header))
                    {
                        break;
                    }
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(header.Split(':', 2)[1].Trim(), out contentLength);
                    }
                }

                if (contentLength > 0)
                {
                    var body = new byte[contentLength];
                    await tls.ReadExactlyAsync(body, stopping.Token).ConfigureAwait(false);
                }

                int requestNumber;
                lock (gate)
                {
                    var parts = requestLine.Split(' ');
                    requests.Add(parts.Length > 1 ? parts[1] : requestLine);
                    requestNumber = requests.Count;
                }

                if (DropRequest?.Invoke(requestNumber) == true)
                {
                    break;
                }

                var responseBytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {Status}\r\nContent-Length: 0\r\n\r\n");
                await tls.WriteAsync(responseBytes, stopping.Token).ConfigureAwait(false);
                await tls.FlushAsync(stopping.Token).ConfigureAwait(false);

                if (HangUpAfterResponse == HangUpMode.CloseNotify)
                {
                    await tls.ShutdownAsync().ConfigureAwait(false);
                    break;
                }
                if (HangUpAfterResponse == HangUpMode.Fin)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or AuthenticationException)
        {
        }
        finally
        {
            try { client.Close(); } catch { }
            closed.Writer.TryWrite(true);
        }
    }

    static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0)
        {
            var ch = (char)buffer[0];
            if (ch == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }
                return sb.ToString();
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static X509Certificate2 CreateCertificate(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        await accepting.ConfigureAwait(false);
        stopping.Dispose();
    }
}
