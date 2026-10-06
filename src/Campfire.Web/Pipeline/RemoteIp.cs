using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>request.remote_ip</c>, as <c>ActionDispatch::RemoteIp::GetIp#calculate_ip</c> works it out
/// (actionpack <c>middleware/remote_ip.rb</c>): the forwarded addresses (<c>Forwarded: for=</c>,
/// else <c>X-Forwarded-For</c>, right to left), then <c>Client-Ip</c>, then the peer, skipping
/// trusted proxies. A forwarded address is believed whoever sends it; <c>remote_ip</c> is only as
/// trustworthy as the proxy in front of the app.
/// </summary>
public static partial class RemoteIp
{
    /// <summary><c>ActionDispatch::RemoteIp::TRUSTED_PROXIES</c></summary>
    public static readonly IReadOnlyList<IpNetwork> TrustedProxies =
    [
        IpNetwork.Parse("127.0.0.0/8"),
        IpNetwork.Parse("::1"),
        IpNetwork.Parse("fc00::/7"),
        IpNetwork.Parse("10.0.0.0/8"),
        IpNetwork.Parse("172.16.0.0/12"),
        IpNetwork.Parse("192.168.0.0/16"),
        IpNetwork.Parse("169.254.0.0/16"),
        IpNetwork.Parse("fe80::/10"),
    ];

    /// <summary>
    /// The client's address, or <c>""</c> when there's none. Throws
    /// <see cref="IpSpoofAttackException"/> when <c>Client-Ip</c> isn't among the forwarded
    /// addresses (<c>ip_spoofing_check</c>, on by default).
    /// </summary>
    public static string Calculate(HttpRequest request, IReadOnlyList<IpNetwork>? proxies = null, bool checkIp = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        var peer = request.HttpContext.Connection.RemoteIpAddress;
        if (peer is { IsIPv4MappedToIPv6: true })
        {
            peer = peer.MapToIPv4();
        }
        return Calculate(
            peer?.ToString(),
            Header(request, "Client-Ip"),
            ForwardedFor(Header(request, "Forwarded"), Header(request, "X-Forwarded-For")),
            proxies ?? TrustedProxies,
            checkIp);
    }

    /// <summary><c>calculate_ip</c> over the raw <c>REMOTE_ADDR</c>, <c>Client-Ip</c> and <c>forwarded_for</c>.</summary>
    public static string Calculate(string? remoteAddrHeader, string? clientIpHeader, IReadOnlyList<string?>? forwardedFor, IReadOnlyList<IpNetwork> proxies, bool checkIp = true)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        var remoteAddrs = Sanitize(IpsFrom(remoteAddrHeader));
        var remoteAddr = remoteAddrs.Count > 0 ? remoteAddrs[^1] : null;
        var clientIps = Sanitize(IpsFrom(clientIpHeader));
        clientIps.Reverse();
        var forwardedIps = Sanitize(forwardedFor ?? []);
        forwardedIps.Reverse();

        if (checkIp && clientIps.Count > 0 && forwardedIps.Count > 0 && !forwardedIps.Contains(clientIps[^1]))
        {
            throw new IpSpoofAttackException($"IP spoofing attack?! HTTP_CLIENT_IP={clientIpHeader} HTTP_X_FORWARDED_FOR=...");
        }

        var ips = new List<string>(forwardedIps);
        ips.AddRange(clientIps);
        foreach (var ip in ips)
        {
            if (!IsProxy(ip, proxies))
            {
                return ip;
            }
        }
        if (remoteAddr is not null && !IsProxy(remoteAddr, proxies))
        {
            return remoteAddr;
        }
        return ips.Count > 0 ? ips[^1] : remoteAddr ?? "";
    }

    /// <summary>
    /// Rack's <c>forwarded_for</c>: the <c>Forwarded</c> header's <c>for=</c> addresses when it has
    /// any, else <c>X-Forwarded-For</c>'s, without ports or brackets (null where one doesn't parse
    /// as an authority). Null when neither header is there.
    /// </summary>
    public static IReadOnlyList<string?>? ForwardedFor(string? forwarded, string? xForwardedFor)
    {
        if (ForwardedHeader.Values(forwarded, "for") is { } fors)
        {
            return fors.Select(Authority.Address).ToList();
        }
        if (xForwardedFor is not null)
        {
            return ForwardedHeader.SplitHeader(xForwardedFor).Select(value => Authority.Address(Authority.WrapIpv6(value))).ToList();
        }
        return null;
    }

    static bool IsProxy(string ip, IReadOnlyList<IpNetwork> proxies)
    {
        var address = IpNetwork.TryParse(ip);
        return address is not null && proxies.Any(proxy => proxy.Contains(address.Value.Address));
    }

    // ips_from: header.strip.split(/[,\s]+/)
    static List<string?> IpsFrom(string? header) =>
        header is null ? [] : [.. IpSeparator().Split(header.Trim(RubyWhitespace)).Where(ip => ip.Length > 0)];

    // sanitize_ips: keep what IPAddr reads as a single address, as written.
    static List<string> Sanitize(IEnumerable<string?> ips) =>
        [.. ips.Where(ip => ip is not null && IpNetwork.TryParse(ip) is { IsSingleAddress: true }).Select(ip => ip!)];

    static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    internal static readonly char[] RubyWhitespace = [' ', '\t', '\n', '\v', '\f', '\r', '\0'];

    [GeneratedRegex(@"[, \t\r\n\f\v]+")]
    private static partial Regex IpSeparator();
}

/// <summary>
/// An <c>IPAddr</c>: an address with a prefix, as Ruby's <c>ipaddr</c> reads <c>"10.0.0.0/8"</c>,
/// <c>"::1"</c> or <c>"[fe80::1%eth0]"</c>. IPv4 must be four decimal octets without leading
/// zeros, as Ruby requires.
/// </summary>
public readonly record struct IpNetwork(IPAddress Address, int PrefixLength)
{
    public bool IsSingleAddress => PrefixLength == (Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

    public static IpNetwork Parse(string text) => TryParse(text) ?? throw new FormatException($"invalid address: {text}");

    public static IpNetwork? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var prefix = (string?)null;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            prefix = text[(slash + 1)..];
            text = text[..slash];
        }
        if (text.Length > 2 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1];
        }
        var address = text.Contains(':', StringComparison.Ordinal) ? ParseIpv6(text) : ParseIpv4(text);
        if (address is null)
        {
            return null;
        }
        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix is null)
        {
            return new IpNetwork(address, bits);
        }
        if (prefix.Length > 0 && prefix.All(char.IsAsciiDigit) && int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length <= bits)
        {
            return new IpNetwork(Mask(address, length), length);
        }
        // A netmask such as "255.255.255.0".
        if (address.AddressFamily == AddressFamily.InterNetwork && ParseIpv4(prefix) is { } mask && MaskLength(mask.GetAddressBytes()) is { } maskLength)
        {
            return new IpNetwork(Mask(address, maskLength), maskLength);
        }
        return null;
    }

    /// <summary><c>IPAddr#include?</c> (<c>===</c>): same family and inside the prefix.</summary>
    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != Address.AddressFamily)
        {
            return false;
        }
        return Mask(address, PrefixLength).Equals(Address);
    }

    static IPAddress? ParseIpv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return null;
        }
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0'))
            {
                return null;
            }
            var value = int.Parse(part, CultureInfo.InvariantCulture);
            if (value > 255)
            {
                return null;
            }
            bytes[i] = (byte)value;
        }
        return new IPAddress(bytes);
    }

    static IPAddress? ParseIpv6(string text)
    {
        var zone = text.IndexOf('%', StringComparison.Ordinal);
        var address = zone >= 0 ? text[..zone] : text;
        if (address.Length == 0 || !address.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.'))
        {
            return null;
        }
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }
        return parsed;
    }

    static IPAddress Mask(IPAddress address, int length)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(length - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }

    static int? MaskLength(byte[] mask)
    {
        var length = 0;
        var ended = false;
        foreach (var b in mask)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (b & (1 << bit)) != 0;
                if (set && ended)
                {
                    return null;
                }
                if (set)
                {
                    length++;
                }
                else
                {
                    ended = true;
                }
            }
        }
        return length;
    }
}

/// <summary>Rack's parsing of the <c>Forwarded</c> and <c>X-Forwarded-*</c> headers.</summary>
public static partial class ForwardedHeader
{
    /// <summary>
    /// <c>Rack::Utils.forwarded_values(header)[token]</c>: the values of one parameter
    /// (<c>for</c>, <c>by</c>, <c>host</c> or <c>proto</c>) in order, or null when the header is
    /// missing, malformed or has none.
    /// </summary>
    public static IReadOnlyList<string>? Values(string? header, string token)
    {
        if (header is null)
        {
            return null;
        }
        var values = new List<string>();
        foreach (var field in header.Replace("\n", ";", StringComparison.Ordinal).Split(';'))
        {
            foreach (var rawPair in field.Split(','))
            {
                var pair = string.Join('=', rawPair.Split('=').Select(part => part.Trim(RemoteIp.RubyWhitespace)));
                var match = ForwardedPair().Match(pair);
                if (!match.Success)
                {
                    return null;
                }
                if (match.Groups[1].Value.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(match.Groups[2].Value);
                }
            }
        }
        return values.Count > 0 ? values : null;
    }

    /// <summary>Rack's <c>split_header</c>: <c>value.strip.split(/[, \t]+/)</c>.</summary>
    public static List<string> SplitHeader(string? value) =>
        value is null ? [] : [.. HeaderSeparator().Split(value.Trim(RemoteIp.RubyWhitespace)).Where(part => part.Length > 0)];

    [GeneratedRegex(@"\A(by|for|host|proto)=""?([^""]+)""?\z", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardedPair();

    [GeneratedRegex(@"[, \t]+")]
    private static partial Regex HeaderSeparator();
}

/// <summary>Rack's <c>split_authority</c> and <c>wrap_ipv6</c>.</summary>
static partial class Authority
{
    /// <summary>The address part (<c>split_authority(authority)[1]</c>), or null when it doesn't parse.</summary>
    public static string? Address(string authority)
    {
        var match = AuthorityPattern().Match(authority);
        if (!match.Success)
        {
            return null;
        }
        return match.Groups["v6"].Success ? match.Groups["v6"].Value : match.Groups["address"].Value;
    }

    public static string WrapIpv6(string host) =>
        !host.StartsWith('[') && host.Count(c => c == ':') > 1 ? $"[{host}]" : host;

    // AUTHORITY: a bracketed IPv6 address or RFC 3986 reg-name characters, then an optional port.
    [GeneratedRegex(@"\A(?:\[(?<v6>[0-9A-Fa-f:.]+(?:%[-0-9A-Za-z._~]+)?)\]|(?<address>[-a-zA-Z0-9._~%!$&'()*+,;=]*?))(?::(?<port>\d+))?\z")]
    private static partial Regex AuthorityPattern();
}
