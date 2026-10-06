using System.Globalization;
using System.Text.RegularExpressions;

namespace Campfire.Data.Lifecycle;

// Ban's `ip_address_is_public` validation (reference/app/models/ban.rb): `IPAddr.new(ip_address)`
// must parse and be none of loopback, private or link-local. Parsing follows Ruby's ipaddr.rb
// (3.4): an optional `/prefix` or `/mask` that masks the address, `[...]` and `%zone` for IPv6,
// and no zero-filled IPv4 octets.
public static partial class BanAddresses
{
    const int ipv4 = 4;
    const int ipv6 = 6;
    static readonly UInt128 Ipv4Mask = uint.MaxValue;
    static readonly UInt128 Ipv6Mask = UInt128.MaxValue;

    public static bool IsPublic(string ipAddress) => Error(ipAddress) is null;

    // The validation's error on `ip_address`, or null when the address is public.
    public static string? Error(string ipAddress)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        if (Parse(ipAddress) is not { } address)
        {
            return "is not a valid IP address";
        }
        return IsLoopback(address) || IsPrivate(address) || IsLinkLocal(address) ? "cannot be a private or internal IP address" : null;
    }

    readonly record struct Address(UInt128 Value, int Family);

    // `IPAddr.new(addr)`; null where it raises.
    static Address? Parse(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var prefix = slash < 0 ? text : text[..slash];
        var prefixLength = slash < 0 ? null : text[(slash + 1)..];
        var family = 0;
        if (Bracketed().Match(prefix) is { Success: true } bracketed)
        {
            prefix = bracketed.Groups[1].Value;
            family = ipv6;
        }
        if (Zoned().Match(prefix) is { Success: true } zoned)
        {
            prefix = zoned.Groups[1].Value;
            family = ipv6;
        }

        Address? address = null;
        if (family is 0 or ipv4)
        {
            if (!TryInAddr(prefix, out var value, out var invalid) && invalid)
            {
                return null;
            }
            if (value is { } v4)
            {
                address = new Address(v4, ipv4);
            }
        }
        if (address is null && family is 0 or ipv6)
        {
            if (InAddr6(prefix) is not { } v6)
            {
                return null;
            }
            address = new Address(v6, ipv6);
        }
        if (address is not { } parsed || (family != 0 && parsed.Family != family))
        {
            return null;
        }
        return prefixLength is null ? parsed : Mask(parsed, prefixLength);
    }

    // `in_addr`: null for something that isn't IPv4-like; `invalid` when it is but raises.
    static bool TryInAddr(string text, out UInt128? value, out bool invalid)
    {
        value = null;
        invalid = false;
        if (!Ipv4Like().IsMatch(text))
        {
            return false;
        }
        var octets = text.Split('.');
        if (OctetsValue(octets) is not { } parsed)
        {
            invalid = true;
            return false;
        }
        value = parsed;
        return true;
    }

    static UInt128? OctetsValue(IReadOnlyList<string> octets)
    {
        UInt128 value = 0;
        foreach (var octet in octets)
        {
            var digits = octet.TrimStart('0');
            if (digits.Length > 3 || (digits.Length > 0 && int.Parse(digits, CultureInfo.InvariantCulture) >= 256))
            {
                return null;
            }
            if (octet != "0" && octet.StartsWith('0'))
            {
                return null;
            }
            value = (value << 8) | (uint)(digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture));
        }
        return value;
    }

    // `in6_addr`
    static UInt128? InAddr6(string text)
    {
        string left;
        string right;
        UInt128 tail;
        if (Ipv6Full().Match(text) is { Success: true } full)
        {
            if (full.Groups[1].Success)
            {
                if (OctetsValue([full.Groups[2].Value, full.Groups[3].Value, full.Groups[4].Value, full.Groups[5].Value]) is not { } v4)
                {
                    return null;
                }
                tail = v4;
                left = full.Groups[1].Value + ":";
            }
            else
            {
                tail = 0;
                left = text;
            }
            right = "";
        }
        else if (Ipv6Compressed().Match(text) is { Success: true } compressed)
        {
            var colons = text.Count(c => c == ':');
            if (compressed.Groups[4].Success)
            {
                if (colons > 6 || OctetsValue([compressed.Groups[4].Value, compressed.Groups[5].Value, compressed.Groups[6].Value, compressed.Groups[7].Value]) is not { } v4)
                {
                    return null;
                }
                tail = v4;
                left = compressed.Groups[1].Value;
                right = compressed.Groups[3].Value + "0:0";
            }
            else
            {
                left = compressed.Groups[1].Value;
                right = compressed.Groups[2].Value;
                if (colons > (left.Length == 0 || right.Length == 0 ? 8 : 7))
                {
                    return null;
                }
                tail = 0;
            }
        }
        else
        {
            return null;
        }

        var l = RubySplit(left);
        var r = RubySplit(right);
        var rest = 8 - l.Count - r.Count;
        if (rest < 0)
        {
            return null;
        }
        UInt128 value = 0;
        foreach (var group in l.Concat(Enumerable.Repeat("0", rest)).Concat(r))
        {
            value = (value << 16) | (group.Length == 0 ? 0 : uint.Parse(group, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return value | tail;
    }

    // `mask!(prefixlen)`
    static Address? Mask(Address address, string mask)
    {
        var bits = address.Family == ipv4 ? 32 : 128;
        int prefixLength;
        if (PrefixLength().IsMatch(mask))
        {
            if (mask.Length > 3 || (prefixLength = int.Parse(mask, CultureInfo.InvariantCulture)) > bits)
            {
                return null;
            }
        }
        else if (AllDigits().IsMatch(mask))
        {
            return null;
        }
        else
        {
            if (Parse(mask) is not { } maskAddress || maskAddress.Family != address.Family)
            {
                return null;
            }
            var full = address.Family == ipv4 ? Ipv4Mask : Ipv6Mask;
            var inverted = maskAddress.Value ^ full;
            if (((inverted + 1) & inverted) != 0)
            {
                return null;
            }
            return address with { Value = address.Value & maskAddress.Value };
        }
        var shift = bits - prefixLength;
        return address with { Value = shift == 128 ? 0 : (address.Value >> shift) << shift };
    }

    static bool IsLoopback(Address a) => a.Family == ipv4
        ? (a.Value & 0xff000000) == 0x7f000000
        : a.Value == 1 || (IsMappedLoosely(a.Value) && (a.Value & 0xff000000) == 0x7f000000);

    static bool IsPrivate(Address a)
    {
        var v4 = a.Value & 0xffffffff;
        var privateV4 = (v4 & 0xff000000) == 0x0a000000 || (v4 & 0xfff00000) == 0xac100000 || (v4 & 0xffff0000) == 0xc0a80000;
        return a.Family == ipv4
            ? privateV4
            : (a.Value >> 121) == (UInt128)0xfc >> 1 || (IsMappedLoosely(a.Value) && privateV4);
    }

    static bool IsLinkLocal(Address a) => a.Family == ipv4
        ? (a.Value & 0xffff0000) == 0xa9fe0000
        : (a.Value >> 118) == (UInt128)0xfe80 >> 6 || (IsMappedLoosely(a.Value) && (a.Value & 0xffff0000) == 0xa9fe0000);

    // `@addr & 0xffff_0000_0000 == 0xffff_0000_0000`: ipaddr.rb checks only these 16 bits.
    static bool IsMappedLoosely(UInt128 value) => (value & 0xffff_0000_0000) == 0xffff_0000_0000;

    // `String#split(':')`: trailing empty fields dropped.
    static List<string> RubySplit(string text)
    {
        var parts = text.Split(':').ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }
        return parts;
    }

    [GeneratedRegex(@"\A\[(.*)\]\z")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\A(.*)(%[A-Za-z0-9_]+)\z")]
    private static partial Regex Zoned();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex Ipv4Like();

    [GeneratedRegex(@"\A(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|((?:[0-9a-f]{1,4}:){6})([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+))\z", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6Full();

    [GeneratedRegex(@"\A((?:(?:[0-9a-f]{1,4}:)*[0-9a-f]{1,4})?)::((?:((?:[0-9a-f]{1,4}:)*)(?:[0-9a-f]{1,4}|([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)))?)\z", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6Compressed();

    [GeneratedRegex(@"\A(0|[1-9]+[0-9]*)\z")]
    private static partial Regex PrefixLength();

    [GeneratedRegex(@"\A[0-9]+\z")]
    private static partial Regex AllDigits();
}
