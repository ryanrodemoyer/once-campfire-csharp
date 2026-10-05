using System.Net;
using System.Net.Sockets;

namespace Campfire.Jobs.RestrictedHttp;

/// <summary>Opens the TCP connection to an already-checked address. Tests redirect it to a local server.</summary>
public delegate ValueTask<Stream> Dialer(IPEndPoint endpoint, CancellationToken cancellationToken);

/// <summary>
/// The HTTP handlers the jobs send through, one per policy. None follows redirects (Net::HTTP
/// doesn't), and each inflates gzip and deflate bodies the way Net::HTTP does.
/// <para>
/// The guarded policies connect only to an address the guard admitted: the request's
/// <see cref="PinnedAddress"/> when the caller resolved one (<c>Net::HTTP#ipaddr=</c>), otherwise
/// the guard's answer for the host. The address is checked again at connect time and the
/// connection goes to exactly that address, so a second DNS answer can't rebind it. They never
/// use a proxy, which would resolve the host itself, and never reuse a connection, so every
/// request goes to the address it was pinned to (Rails opens a fresh Net::HTTP per pinned request).
/// </para>
/// </summary>
public static class RestrictedHttpHandlers
{
    /// <summary>Net::HTTP's default open timeout, which Opengraph::Fetch and the web-push gem keep.</summary>
    public static readonly TimeSpan NetHttpDefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary><c>Webhook::ENDPOINT_TIMEOUT</c></summary>
    public static readonly TimeSpan WebhookTimeout = TimeSpan.FromSeconds(7);

    /// <summary>The public address the request must connect to, from <see cref="PrivateNetworkGuard.ResolveAsync"/>.</summary>
    public static readonly HttpRequestOptionsKey<IPAddress> PinnedAddress = new("Campfire.RestrictedHttp.PinnedAddress");

    /// <summary><c>Opengraph::Fetch</c>: http or https on any port, through the guard.</summary>
    public static SocketsHttpHandler OpenGraph(PrivateNetworkGuard guard, Dialer? dial = null) =>
        Guarded(GuardedConnect(guard, dial ?? DialTcpAsync, httpsOnly: false));

    /// <summary>
    /// <c>WebPush::PersistentRequest</c> with an <c>endpoint_ip</c>: https on port 443 only
    /// (<c>Push::Subscription#permitted_endpoint_uri?</c>), through the guard.
    /// </summary>
    public static SocketsHttpHandler WebPush(PrivateNetworkGuard guard, Dialer? dial = null) =>
        Guarded(GuardedConnect(guard, dial ?? DialTcpAsync, httpsOnly: true));

    /// <summary>
    /// <c>Webhook#http</c>: unrestricted by design. Only an administrator sets a bot's webhook URL,
    /// and operators legitimately point bots at their own internal services. Like
    /// <c>Net::HTTP.new(host, port)</c> it honours the environment's proxy settings.
    /// </summary>
    public static SocketsHttpHandler Webhook() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = WebhookTimeout,
        UseCookies = false,
    };

    static SocketsHttpHandler Guarded(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connect) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectCallback = connect,
        ConnectTimeout = NetHttpDefaultTimeout,
        PooledConnectionLifetime = TimeSpan.Zero,
        UseCookies = false,
        UseProxy = false,
    };

    static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> GuardedConnect(PrivateNetworkGuard guard, Dialer dial, bool httpsOnly) =>
        async (context, cancellationToken) =>
        {
            var request = context.InitialRequestMessage;
            var host = context.DnsEndPoint.Host;
            if (httpsOnly && (request.RequestUri?.Scheme != Uri.UriSchemeHttps || context.DnsEndPoint.Port != 443))
            {
                throw PrivateNetworkViolationException.For(host);
            }

            var address = request.Options.TryGetValue(PinnedAddress, out var pinned)
                ? pinned
                : await guard.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            if (Surfguard.IsBlockedAddress(address))
            {
                throw PrivateNetworkViolationException.For(host);
            }
            return await dial(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
        };

    static async ValueTask<Stream> DialTcpAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
