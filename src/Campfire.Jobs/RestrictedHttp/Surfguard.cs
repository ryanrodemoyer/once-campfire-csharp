using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Campfire.Jobs.RestrictedHttp;

/// <summary>
/// The parts of the surfguard gem (basecamp/surfguard 59e278c, default policy) that
/// <c>RestrictedHTTP::PrivateNetworkGuard</c> calls: <c>resolve_public_ips</c> and
/// <c>blocked_address?</c>. Numeric hosts never reach DNS, names must be plain LDH labels, and
/// every answer is classified with the blocked ones dropped.
/// </summary>
public static partial class Surfguard
{
    public const int MaxHostBytes = 255;
    public const int MaxAddresses = 256;

    static readonly IpAddr[] IanaAllocatedIpv6Unicast = Cidrs(
        "2001::/23", "2001:200::/23", "2001:400::/23", "2001:600::/23", "2001:800::/22",
        "2001:c00::/23", "2001:e00::/23", "2001:1200::/23", "2001:1400::/22", "2001:1800::/23",
        "2001:1a00::/23", "2001:1c00::/22", "2001:2000::/19", "2001:4000::/23", "2001:4200::/23",
        "2001:4400::/23", "2001:4600::/23", "2001:4800::/23", "2001:4a00::/23", "2001:4c00::/23",
        "2001:5000::/20", "2001:8000::/19", "2001:a000::/20", "2001:b000::/20", "2002::/16",
        "2003::/18", "2400::/12", "2410::/12", "2600::/12", "2610::/23", "2620::/23", "2630::/12",
        "2800::/12", "2a00::/12", "2a10::/12", "2c00::/12");

    // IPAddr#private?, #loopback? and #link_local? add nothing for IPv4: their ranges are all here.
    static readonly IpAddr[] DisallowedIpv4 = Cidrs(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
        "168.63.129.16/32", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24",
        "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15",
        "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4");

    static readonly IpAddr[] DisallowedIpv6 = Cidrs(
        "::/128", "100::/64", "100:0:0:1::/64", "2001::/32", "2001:2::/48",
        "2001:db8::/32", "2002::/16", "3fff::/20", "5f00::/16", "fec0::/10", "ff00::/8");

    static readonly IpAddr IetfProtocolAssignments = IpAddr.Cidr("2001::/23");
    static readonly IpAddr[] GloballyReachableIetfAssignments = Cidrs("2001:3::/32", "2001:4:112::/48");
    static readonly IpAddr Nat64WellKnown = IpAddr.Cidr("64:ff9b::/96");
    static readonly IpAddr Nat64LocalUse = IpAddr.Cidr("64:ff9b:1::/48");
    static readonly IpAddr Ipv4Mapped = IpAddr.Cidr("::ffff:0:0/96");
    static readonly IpAddr Ipv4Translatable = IpAddr.Cidr("::ffff:0:0:0/96");
    static readonly IpAddr Ipv4Compatible = IpAddr.Cidr("::/96");
    static readonly IpAddr UniqueLocal = IpAddr.Cidr("fc00::/7");
    static readonly IpAddr LinkLocalV6 = IpAddr.Cidr("fe80::/10");
    static readonly IpAddr LoopbackV6 = IpAddr.Cidr("::1/128");

    /// <summary>
    /// <c>Surfguard.resolve_public_ips(host)</c>: every admitted address, IPv4 before IPv6 and in
    /// resolver order within each family. Malformed input comes back empty; a lookup that fails or
    /// finds nothing throws <see cref="UnresolvableHostException"/>.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolvePublicIpsAsync(IResolver resolver, string host, CancellationToken cancellationToken = default)
    {
        if (!IsNormalHost(host))
        {
            return [];
        }

        var answers = NumericLiterals(host, out var literal) switch
        {
            HostKind.Malformed => null,
            HostKind.Literal => [literal],
            _ => await LookupAsync(resolver, host, cancellationToken).ConfigureAwait(false),
        };
        if (answers is null)
        {
            return [];
        }

        var admitted = NormalizeAnswers(answers).Where(ip => !IsBlocked(ip)).ToList();
        return [.. admitted.Where(ip => ip.IsV4).Concat(admitted.Where(ip => !ip.IsV4)).Select(ip => ip.ToIPAddress())];
    }

    /// <summary><c>Surfguard.blocked_address?(ip)</c>. Anything that isn't one address is blocked.</summary>
    public static bool IsBlockedAddress(string ip) =>
        !IsOwnedString(ip) || IpAddr.Parse(ip) is not { IsHost: true } address || IsBlocked(address);

    /// <summary><c>Surfguard.blocked_address?(ip)</c>. A zoned address is blocked.</summary>
    public static bool IsBlockedAddress(IPAddress ip) => IsZoned(ip) || IsBlocked(IpAddr.From(ip));

    static bool IsZoned(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0;

    static bool IsBlocked(IpAddr ip)
    {
        if (ip.IsV4)
        {
            return IsDisallowedIpv4(ip);
        }
        if (Ipv4Mapped.Contains(ip) || Ipv4Compatible.Contains(ip) || Nat64LocalUse.Contains(ip))
        {
            return true;
        }
        if (Nat64WellKnown.Contains(ip) || Ipv4Translatable.Contains(ip))
        {
            return IsDisallowedIpv4(IpAddr.Host(ip.Value & uint.MaxValue, isV4: true));
        }
        return IsDisallowedIpv6(ip);
    }

    static bool IsDisallowedIpv4(IpAddr ip) => DisallowedIpv4.Any(range => range.Contains(ip));

    static bool IsDisallowedIpv6(IpAddr ip)
    {
        if (GloballyReachableIetfAssignments.Any(range => range.Contains(ip)))
        {
            return false;
        }
        if (UniqueLocal.Contains(ip) || LoopbackV6.Contains(ip) || LinkLocalV6.Contains(ip) || IetfProtocolAssignments.Contains(ip))
        {
            return true;
        }
        if (DisallowedIpv6.Any(range => range.Contains(ip)))
        {
            return true;
        }
        return !IanaAllocatedIpv6Unicast.Any(range => range.Contains(ip));
    }

    static async Task<IpAddr[]> LookupAsync(IResolver resolver, string host, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new UnresolvableHostException();
        }

        if (addresses.Count > MaxAddresses || addresses.Any(IsZoned))
        {
            throw new UnresolvableHostException();
        }
        return [.. addresses.Select(IpAddr.From)];
    }

    /// <summary><c>normalize_answers</c>: deduplicated in order, and at least one.</summary>
    static List<IpAddr> NormalizeAnswers(IEnumerable<IpAddr> answers)
    {
        var unique = answers.Distinct().ToList();
        return unique.Count == 0 ? throw new UnresolvableHostException() : unique;
    }

    /// <summary><c>normalize_host</c> for a string: ASCII, no NUL, 1 to 255 bytes, no zone.</summary>
    static bool IsNormalHost(string host) =>
        IsOwnedString(host) && host.Length > 0 && host.Length <= MaxHostBytes && !host.Contains('%', StringComparison.Ordinal);

    /// <summary><c>owned_string</c>: ASCII without NUL.</summary>
    static bool IsOwnedString(string text) => text.All(c => c is > '\0' and < '\x80');

    enum HostKind { Malformed, Literal, Name }

    /// <summary>
    /// <c>numeric_literals</c>: <c>getaddrinfo(host, AI_NUMERICHOST)</c> (glibc's <c>inet_aton</c>
    /// and <c>inet_pton</c>), then an <c>IPAddr</c> literal, else a name for DNS unless it looks numeric.
    /// </summary>
    static HostKind NumericLiterals(string host, out IpAddr literal)
    {
        literal = default;
        if (!IsValidHostSyntax(host) || IsMalformedNumericHostCandidate(host))
        {
            return HostKind.Malformed;
        }
        if (GlibcNumericHost.Parse(host) is { } numeric)
        {
            literal = numeric;
            return HostKind.Literal;
        }
        if (IpAddr.Parse(host) is { IsHost: true } address)
        {
            literal = address;
            return HostKind.Literal;
        }
        return IsNumericHostCandidate(host) ? HostKind.Malformed : HostKind.Name;
    }

    static bool IsValidHostSyntax(string host)
    {
        if (host.Contains(':', StringComparison.Ordinal) || IsLegacyIpv4Shape(host) || IsFullWidthHostLiteral(host))
        {
            return true;
        }
        var name = host.EndsWith('.') ? host[..^1] : host;
        return name.Split('.').All(label => label.Length <= 63 && HostLabel().IsMatch(label));
    }

    static bool IsNumericHostCandidate(string host) => host.Contains(':', StringComparison.Ordinal) || IsLegacyIpv4Shape(host);

    static bool IsMalformedNumericHostCandidate(string host)
    {
        if (host.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }
        var core = host.TrimStart('%', '/');
        var end = core.IndexOfAny(['%', '/']);
        if (end >= 0)
        {
            core = core[..end];
        }
        var malformed = core != host || (core.Length > 0 && core.Split('.').Any(label => label.Length == 0));
        return malformed && IsLegacyIpv4Shape(core) && !IsFullWidthHostLiteral(host);
    }

    /// <summary>One to four dot-separated (empty parts ignored) decimal or 0x-hex numbers.</summary>
    static bool IsLegacyIpv4Shape(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is >= 1 and <= 4 && parts.All(part => LegacyIpv4Part().IsMatch(part));
    }

    static bool IsFullWidthHostLiteral(string text) => IpAddr.Parse(text) is { IsHost: true };

    static IpAddr[] Cidrs(params string[] cidrs) => [.. cidrs.Select(IpAddr.Cidr)];

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z")]
    private static partial Regex HostLabel();

    [GeneratedRegex(@"\A(?:0[xX][0-9A-Fa-f]+|[0-9]+)\z")]
    private static partial Regex LegacyIpv4Part();
}
