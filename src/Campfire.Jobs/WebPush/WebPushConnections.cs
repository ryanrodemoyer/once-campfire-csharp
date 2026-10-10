using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Campfire.Jobs.RestrictedHttp;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>WebPush::Connections</c> (reference/lib/web_push/connections.rb):
/// Keeps TLS connections to push services open between deliveries, so a push doesn't pay for a new TCP and TLS
/// handshake every time. Each connection is pinned to the address Push::Subscription resolved and guarded for the
/// delivery that opened it, and is handed out again only to a delivery whose own resolution, just made, returned that
/// same address for the same host.
/// </summary>
public sealed class WebPushConnections : IDisposable
{
    public sealed class ConnectionLostException(string message) : Exception(message);
    public sealed class StaleConnectionException() : Exception();

    public enum Stage
    {
        Init,
        Checking,
        Connecting,
        Sent
    }

    /// <summary>Base descriptor for Net::HTTP-like connections.</summary>
    public class NetHttp
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string? IpAddress { get; set; }
        public bool UseSsl { get; set; } = true;
        public bool IsProxy { get; set; }
        public bool IsStarted { get; set; }
        public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
        public Dialer? Dialer { get; set; }

        public NetHttp(string host, int port, string? proxyAddress = null)
        {
            Host = host;
            Port = port;
            IsProxy = proxyAddress is not null;
        }

        public void Start() => IsStarted = true;
        public void Finish() => IsStarted = false;
    }

    /// <summary>
    /// <c>WebPush::Connections::HTTP</c>: The Net::HTTP a pooled delivery is opened with.
    /// </summary>
    public class HTTP : NetHttp
    {
        public HTTP(string host, int port, string? proxyAddress = null)
            : base(host, port, proxyAddress)
        {
        }
    }

    public sealed record PushRequest(string Method, string Path, IReadOnlyList<(string Name, string Value)> Headers, byte[] Body);

    public sealed record PushResponse(int StatusCode, string ReasonPhrase, IReadOnlyList<(string Name, string Value)> Headers, byte[] Body);

    sealed class ActiveConnection
    {
        public Socket? Socket { get; }
        public Stream Stream { get; }
        public Stage Stage { get; set; } = Stage.Init;
        public double IdleSince { get; set; }

        public ActiveConnection(Socket? socket, Stream stream)
        {
            Socket = socket;
            Stream = stream;
        }

        public void Close()
        {
            try { Stream.Dispose(); } catch { }
            try { Socket?.Dispose(); } catch { }
        }
    }

    readonly Lock gate = new();
    readonly Dictionary<(string Host, int Port, string IpAddress), List<ActiveConnection>> idle = [];
    int pid = Environment.ProcessId;
    bool isShutDown;

    public TimeSpan KeepAliveTimeout { get; set; }
    public int MaxIdle { get; set; }

    public WebPushConnections(int keepAliveTimeout = 30, int maxIdle = 150)
    {
        KeepAliveTimeout = TimeSpan.FromSeconds(keepAliveTimeout);
        MaxIdle = maxIdle;
    }

    public PushResponse Request(NetHttp http, PushRequest request, CancellationToken cancellationToken = default) =>
        RequestAsync(http, request, cancellationToken).GetAwaiter().GetResult();

    public async Task<PushResponse> RequestAsync(NetHttp http, PushRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        if (http is not HTTP || string.IsNullOrEmpty(http.IpAddress) || http.IsProxy || !http.UseSsl || http.IsStarted)
        {
            throw new ArgumentException("Only new, direct TLS connections pinned to an address and opened as WebPush::Connections::HTTP are pooled");
        }

        var address = (http.Host, http.Port, http.IpAddress);

        if (Checkout(address) is { } idleConn)
        {
            try
            {
                var response = await SendOverAsync(idleConn, (HTTP)http, request, reused: true, cancellationToken).ConfigureAwait(false);
                Checkin(address, idleConn);
                return response;
            }
            catch (StaleConnectionException)
            {
                // The push service had closed it: a new connection takes the push
            }
        }

        var newConn = await OpenConnectionAsync((HTTP)http, cancellationToken).ConfigureAwait(false);
        var resp = await SendOverAsync(newConn, (HTTP)http, request, reused: false, cancellationToken).ConfigureAwait(false);
        Checkin(address, newConn);
        return resp;
    }

    public void Shutdown()
    {
        lock (gate)
        {
            ForgetAfterFork();
            isShutDown = true;
            foreach (var list in idle.Values)
            {
                foreach (var conn in list)
                {
                    conn.Close();
                }
                list.Clear();
            }
            idle.Clear();
        }
    }

    public void Dispose() => Shutdown();

    ActiveConnection? Checkout((string Host, int Port, string IpAddress) address)
    {
        lock (gate)
        {
            ForgetAfterFork();
            CloseExpired();
            if (idle.TryGetValue(address, out var list) && list.Count > 0)
            {
                var last = list[^1];
                list.RemoveAt(list.Count - 1);
                return last;
            }
            return null;
        }
    }

    void Checkin((string Host, int Port, string IpAddress) address, ActiveConnection connection)
    {
        lock (gate)
        {
            CloseExpired();
            var totalIdle = idle.Values.Sum(l => l.Count);
            if (isShutDown || totalIdle >= MaxIdle)
            {
                connection.Close();
            }
            else
            {
                if (!idle.TryGetValue(address, out var list))
                {
                    list = [];
                    idle[address] = list;
                }
                connection.IdleSince = GetMonotonicTime();
                list.Add(connection);
            }
        }
    }

    void ForgetAfterFork()
    {
        if (pid != Environment.ProcessId)
        {
            idle.Clear();
            pid = Environment.ProcessId;
        }
    }

    void CloseExpired()
    {
        var now = GetMonotonicTime();
        foreach (var (addr, list) in idle)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var conn = list[i];
                if (now - conn.IdleSince > KeepAliveTimeout.TotalSeconds)
                {
                    conn.Close();
                    list.RemoveAt(i);
                }
            }
        }
    }

    static async Task<ActiveConnection> OpenConnectionAsync(HTTP http, CancellationToken cancellationToken)
    {
        Stream stream;
        Socket? rawSocket = null;
        if (http.Dialer is not null)
        {
            stream = await http.Dialer(new IPEndPoint(IPAddress.Parse(http.IpAddress!), http.Port), cancellationToken).ConfigureAwait(false);
            if (stream is NetworkStream ns)
            {
                rawSocket = ns.Socket;
            }
        }
        else
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            rawSocket = socket;
            await socket.ConnectAsync(IPAddress.Parse(http.IpAddress!), http.Port, cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        var ssl = new SslStream(stream, false, http.CertificateValidationCallback);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = http.Host,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or SocketException or IOException)
        {
            ssl.Dispose();
            stream.Dispose();
            rawSocket?.Dispose();
            throw new WebPushOpenSslException("OpenSSL::SSL::SSLError", ex.Message, ex);
        }

        return new ActiveConnection(rawSocket, ssl);
    }

    static async Task<PushResponse> SendOverAsync(ActiveConnection connection, HTTP http, PushRequest request, bool reused, CancellationToken cancellationToken)
    {
        if (reused)
        {
            connection.Stage = Stage.Checking;
            bool isDead = false;
            if (connection.Socket is not null)
            {
                try
                {
                    if (connection.Socket.Poll(0, SelectMode.SelectRead) && connection.Socket.Available == 0)
                    {
                        isDead = true;
                    }
                }
                catch
                {
                    isDead = true;
                }
            }
            if (isDead)
            {
                connection.Close();
                throw new StaleConnectionException();
            }
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append($"{request.Method} {request.Path} HTTP/1.1\r\n");
            sb.Append($"Host: {http.Host}\r\n");
            foreach (var (name, value) in request.Headers)
            {
                sb.Append($"{name}: {value}\r\n");
            }
            if (!request.Headers.Any(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append($"Content-Length: {request.Body.Length}\r\n");
            }
            sb.Append("\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await connection.Stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            if (request.Body.Length > 0)
            {
                await connection.Stream.WriteAsync(request.Body, cancellationToken).ConfigureAwait(false);
            }
            await connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            connection.Stage = Stage.Sent;

            return await ReadResponseAsync(connection.Stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not StaleConnectionException && error is not ConnectionLostException && error is not WebPushOpenSslException)
        {
            connection.Close();
            if (reused && connection.Stage == Stage.Checking)
            {
                throw new StaleConnectionException();
            }
            if (connection.Stage == Stage.Sent)
            {
                throw new ConnectionLostException($"{error.GetType().Name}: {error.Message}");
            }
            throw;
        }
    }

    static async Task<PushResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(line))
        {
            throw new IOException("Empty response from server");
        }
        var parts = line.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new IOException($"Invalid HTTP response status line: {line}");
        }
        var reasonPhrase = parts.Length > 2 ? parts[2].Trim() : "";

        var headers = new List<(string Name, string Value)>();
        int contentLength = 0;
        while (true)
        {
            var headerLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerLine))
            {
                break;
            }
            var idx = headerLine.IndexOf(':');
            if (idx > 0)
            {
                var name = headerLine[..idx].Trim();
                var value = headerLine[(idx + 1)..].Trim();
                headers.Add((name, value));
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                }
            }
        }

        byte[] body = [];
        if (contentLength > 0)
        {
            body = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new IOException("Connection closed prematurely while reading response body");
                }
                read += n;
            }
        }

        return new PushResponse(statusCode, reasonPhrase, headers, body);
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

    static double GetMonotonicTime() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
