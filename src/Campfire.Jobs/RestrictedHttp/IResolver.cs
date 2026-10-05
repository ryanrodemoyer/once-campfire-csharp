using System.Net;

namespace Campfire.Jobs.RestrictedHttp;

/// <summary>Name resolution, so tests can substitute fixed answers.</summary>
public interface IResolver
{
    /// <summary>Every address for <paramref name="host"/>. A failed lookup throws <see cref="System.Net.Sockets.SocketException"/>.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// The system resolver (<c>getaddrinfo</c>: /etc/hosts, then DNS), where Ruby's
/// <c>Resolv.getaddresses</c> reads /etc/hosts, then DNS.
/// </summary>
public sealed class SystemResolver : IResolver
{
    public static SystemResolver Instance { get; } = new();

    SystemResolver()
    {
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}
