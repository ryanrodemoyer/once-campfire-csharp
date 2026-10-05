using System.Net;

namespace Campfire.Jobs.RestrictedHttp;

/// <summary>
/// <c>RestrictedHTTP::PrivateNetworkGuard</c> (reference/lib/restricted_http/private_network_guard.rb):
/// a bare hostname in, the public address to pin out.
/// </summary>
public sealed class PrivateNetworkGuard(IResolver resolver)
{
    public static PrivateNetworkGuard System { get; } = new(SystemResolver.Instance);

    /// <summary>
    /// The first public address <paramref name="hostname"/> resolves to. Throws
    /// <see cref="PrivateNetworkViolationException"/> when it only resolves to blocked addresses
    /// (or isn't a valid host), and lets <see cref="UnresolvableHostException"/> through when it
    /// resolves to nothing, so a DNS miss is never reported as an SSRF attempt.
    /// </summary>
    public async Task<IPAddress> ResolveAsync(string hostname, CancellationToken cancellationToken = default)
    {
        var addresses = await Surfguard.ResolvePublicIpsAsync(resolver, hostname, cancellationToken).ConfigureAwait(false);
        return addresses.Count > 0 ? addresses[0] : throw PrivateNetworkViolationException.For(hostname);
    }

    /// <summary><c>private_ip?(ip)</c>: true for anything that isn't a public address, invalid input included.</summary>
    public static bool IsPrivateIp(string ip) => Surfguard.IsBlockedAddress(ip);

    public static bool IsPrivateIp(IPAddress ip) => Surfguard.IsBlockedAddress(ip);
}
