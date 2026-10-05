using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Campfire.Jobs.RestrictedHttp;

/// <summary>
/// An address and mask the way Ruby's <c>IPAddr</c> (ipaddr 1.2.7, bundled with Ruby 3.4) holds
/// them. Surfguard parses every literal through <c>IPAddr.new</c>, so the same spellings have to be
/// accepted and refused here: brackets, <c>/prefix</c> and <c>/netmask</c> suffixes, embedded IPv4.
/// </summary>
readonly partial record struct IpAddr(UInt128 Value, bool IsV4, UInt128 Mask)
{
    static readonly UInt128 V4Full = uint.MaxValue;

    public UInt128 FullMask => IsV4 ? V4Full : UInt128.MaxValue;

    /// <summary><c>host_address?</c>: a full-width prefix, so exactly one address.</summary>
    public bool IsHost => Mask == FullMask;

    public bool Contains(IpAddr other) => other.IsV4 == IsV4 && (other.Value & Mask) == Value;

    public static IpAddr Host(UInt128 value, bool isV4) => new(value, isV4, isV4 ? V4Full : UInt128.MaxValue);

    public static IpAddr From(IPAddress address)
    {
        var value = UInt128.Zero;
        foreach (var b in address.GetAddressBytes())
        {
            value = (value << 8) | b;
        }
        return Host(value, address.AddressFamily == AddressFamily.InterNetwork);
    }

    public IPAddress ToIPAddress()
    {
        var bytes = new byte[IsV4 ? 4 : 16];
        var value = Value;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xff);
            value >>= 8;
        }
        return new IPAddress(bytes);
    }

    /// <summary>A range constant, such as <c>"10.0.0.0/8"</c>.</summary>
    public static IpAddr Cidr(string text) => Parse(text) ?? throw new ArgumentException($"Invalid CIDR {text}", nameof(text));

    /// <summary>
    /// <c>IPAddr.new(text)</c>, or null where it raises. Zones (<c>%eth0</c>) are refused outright:
    /// Surfguard rejects any address that carries one.
    /// </summary>
    public static IpAddr? Parse(string text)
    {
        if (text.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var prefix = slash < 0 ? text : text[..slash];
        var prefixLength = slash < 0 ? null : text[(slash + 1)..];

        var bracketed = prefix.Length >= 2 && prefix[0] == '[' && prefix[^1] == ']' && !prefix.Contains('\n', StringComparison.Ordinal);
        if (bracketed)
        {
            prefix = prefix[1..^1];
        }

        IpAddr address;
        if (!bracketed && Ipv4Like().IsMatch(prefix))
        {
            if (InAddr(prefix.Split('.')) is not { } v4)
            {
                return null;
            }
            address = Host(v4, isV4: true);
        }
        else if (In6Addr(prefix) is { } v6)
        {
            address = Host(v6, isV4: false);
        }
        else
        {
            return null;
        }

        return prefixLength is null ? address : WithMask(address, prefixLength);
    }

    /// <summary><c>IPAddr#mask!</c> with a prefix length or a netmask.</summary>
    static IpAddr? WithMask(IpAddr address, string mask)
    {
        UInt128 maskBits;
        if (PrefixLength().IsMatch(mask))
        {
            var bits = address.IsV4 ? 32 : 128;
            var length = mask.Length > 3 ? int.MaxValue : int.Parse(mask, CultureInfo.InvariantCulture);
            if (length > bits)
            {
                return null;
            }
            maskBits = length == 0 ? UInt128.Zero : address.FullMask >> (bits - length) << (bits - length);
        }
        else if (Digits().IsMatch(mask))
        {
            return null; // leading zeros in prefix
        }
        else
        {
            if (Parse(mask) is not { } netmask || netmask.IsV4 != address.IsV4)
            {
                return null;
            }
            var n = netmask.Value ^ netmask.Mask;
            if (((n + 1) & n) != UInt128.Zero)
            {
                return null; // not a contiguous mask
            }
            maskBits = netmask.Value;
        }
        return address with { Value = address.Value & maskBits, Mask = maskBits };
    }

    /// <summary><c>IPAddr#in_addr</c>: four decimal octets, none above 255 or zero-filled.</summary>
    static UInt128? InAddr(IReadOnlyList<string> octets)
    {
        var value = UInt128.Zero;
        foreach (var octet in octets)
        {
            var n = octet.Length > 3 ? int.MaxValue : int.Parse(octet, CultureInfo.InvariantCulture);
            if (n > 255)
            {
                return null;
            }
            if (octet != "0" && octet.StartsWith('0'))
            {
                return null;
            }
            value = (value << 8) | (uint)n;
        }
        return value;
    }

    /// <summary><c>IPAddr#in6_addr</c></summary>
    static UInt128? In6Addr(string text)
    {
        string left, right;
        UInt128 embedded = UInt128.Zero;

        if (Ipv6Full().Match(text) is { Success: true } full)
        {
            if (full.Groups[2].Success)
            {
                if (InAddr([full.Groups[2].Value, full.Groups[3].Value, full.Groups[4].Value, full.Groups[5].Value]) is not { } v4)
                {
                    return null;
                }
                embedded = v4;
                left = full.Groups[1].Value + ":";
            }
            else
            {
                left = text;
            }
            right = "";
        }
        else if (Ipv6Compressed().Match(text) is { Success: true } compressed)
        {
            var colons = text.Count(c => c == ':');
            var before = compressed.Groups[1].Value;
            var after = compressed.Groups[2].Value;
            if (compressed.Groups[4].Success)
            {
                if (colons > 6)
                {
                    return null;
                }
                if (InAddr([compressed.Groups[4].Value, compressed.Groups[5].Value, compressed.Groups[6].Value, compressed.Groups[7].Value]) is not { } v4)
                {
                    return null;
                }
                embedded = v4;
                left = before;
                right = compressed.Groups[3].Value + "0:0";
            }
            else
            {
                if (colons > (before.Length == 0 || after.Length == 0 ? 8 : 7))
                {
                    return null;
                }
                left = before;
                right = after;
            }
        }
        else
        {
            return null;
        }

        var l = RubySplit(left);
        var r = RubySplit(right);
        var rest = 8 - l.Length - r.Length;
        if (rest < 0)
        {
            return null;
        }

        var value = UInt128.Zero;
        foreach (var group in l.Concat(Enumerable.Repeat("0", rest)).Concat(r))
        {
            value = (value << 16) | Convert.ToUInt16(group, 16);
        }
        return value | embedded;
    }

    /// <summary><c>String#split(":")</c>, which drops trailing empty fields.</summary>
    static string[] RubySplit(string text)
    {
        var parts = text.Split(':').ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }
        return [.. parts];
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex Ipv4Like();

    [GeneratedRegex(@"\A(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|((?:[0-9a-f]{1,4}:){6})([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+))\z", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6Full();

    [GeneratedRegex(@"\A((?:(?:[0-9a-f]{1,4}:)*[0-9a-f]{1,4})?)::((?:((?:[0-9a-f]{1,4}:)*)(?:[0-9a-f]{1,4}|([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)))?)\z", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6Compressed();

    [GeneratedRegex(@"\A(?:0|[1-9][0-9]*)\z")]
    private static partial Regex PrefixLength();

    [GeneratedRegex(@"\A[0-9]+\z")]
    private static partial Regex Digits();
}
