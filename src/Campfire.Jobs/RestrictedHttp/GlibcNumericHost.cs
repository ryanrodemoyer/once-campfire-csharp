namespace Campfire.Jobs.RestrictedHttp;

/// <summary>
/// glibc's <c>getaddrinfo(host, AI_NUMERICHOST)</c>, which Surfguard asks first: <c>__inet_aton_exact</c>
/// for IPv4 (the legacy <c>127.1</c>, <c>0x7f000001</c> and <c>0177.0.0.1</c> spellings included),
/// otherwise <c>inet_pton6</c>. Ported from resolv/inet_addr.c and resolv/inet_pton.c.
/// </summary>
static class GlibcNumericHost
{
    public static IpAddr? Parse(string host) =>
        InetAtonExact(host) is { } v4 ? IpAddr.Host(v4, isV4: true)
        : InetPton6(host) is { } v6 ? IpAddr.Host(v6, isV4: false)
        : null;

    /// <summary>
    /// 1 to 4 parts, each a C integer (<c>0x</c> hex, leading-zero octal, or decimal) read by
    /// <c>strtoul(cp, &amp;endp, 0)</c>; the last part fills the remaining bytes, and nothing may follow.
    /// </summary>
    static uint? InetAtonExact(string text)
    {
        ReadOnlySpan<ulong> max = [0xffffffff, 0xffffff, 0xffff, 0xff];
        Span<byte> parts = stackalloc byte[3];
        var count = 0;
        var i = 0;
        while (true)
        {
            if (i >= text.Length || !char.IsAsciiDigit(text[i]))
            {
                return null;
            }
            if (Strtoul(text, ref i) is not { } value)
            {
                return null;
            }
            if (i < text.Length && text[i] == '.')
            {
                if (count > 2 || value > 0xff)
                {
                    return null;
                }
                parts[count++] = (byte)value;
                i++;
                continue;
            }
            if (i != text.Length || value > max[count])
            {
                return null;
            }
            var address = (uint)value;
            for (var p = 0; p < count; p++)
            {
                address |= (uint)parts[p] << (24 - (8 * p));
            }
            return address;
        }
    }

    /// <summary><c>strtoul</c> with base 0 from a digit: null once the value passes 32 bits.</summary>
    static ulong? Strtoul(string text, ref int i)
    {
        var radix = 10;
        if (text[i] == '0')
        {
            // "0x" without a hex digit after it reads as 0 and leaves the "x" behind.
            if (i + 2 < text.Length && text[i + 1] is 'x' or 'X' && char.IsAsciiHexDigit(text[i + 2]))
            {
                radix = 16;
                i += 2;
            }
            else
            {
                radix = 8;
            }
        }

        ulong value = 0;
        for (; i < text.Length; i++)
        {
            var digit = DigitValue(text[i]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }
            value = (value * (ulong)radix) + (ulong)digit;
            if (value > 0xffffffff)
            {
                return null;
            }
        }
        return value;
    }

    static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary><c>inet_pton6</c>: hex groups of up to four digits, one <c>::</c>, an optional dotted-quad tail.</summary>
    static UInt128? InetPton6(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        var tp = 0;
        var colonp = -1;
        var src = 0;
        if (text[0] == ':')
        {
            src++;
            if (src == text.Length || text[src] != ':')
            {
                return null;
            }
        }

        var curtok = src;
        var xdigitsSeen = 0;
        var val = 0;
        while (src < text.Length)
        {
            var ch = text[src++];
            var digit = DigitValue(ch);
            if (digit >= 0)
            {
                if (xdigitsSeen == 4)
                {
                    return null;
                }
                val = (val << 4) | digit;
                xdigitsSeen++;
                continue;
            }
            if (ch == ':')
            {
                curtok = src;
                if (xdigitsSeen == 0)
                {
                    if (colonp >= 0)
                    {
                        return null;
                    }
                    colonp = tp;
                    continue;
                }
                if (src == text.Length || tp + 2 > 16)
                {
                    return null;
                }
                bytes[tp++] = (byte)(val >> 8);
                bytes[tp++] = (byte)val;
                xdigitsSeen = 0;
                val = 0;
                continue;
            }
            if (ch == '.' && tp + 4 <= 16 && InetPton4(text.AsSpan(curtok), bytes[tp..]))
            {
                tp += 4;
                xdigitsSeen = 0;
                break;
            }
            return null;
        }

        if (xdigitsSeen > 0)
        {
            if (tp + 2 > 16)
            {
                return null;
            }
            bytes[tp++] = (byte)(val >> 8);
            bytes[tp++] = (byte)val;
        }
        if (colonp >= 0)
        {
            if (tp == 16)
            {
                return null;
            }
            var n = tp - colonp;
            bytes.Slice(colonp, n).CopyTo(bytes[(16 - n)..]);
            bytes[colonp..(16 - n)].Clear();
            tp = 16;
        }
        if (tp != 16)
        {
            return null;
        }

        var value = UInt128.Zero;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }
        return value;
    }

    /// <summary><c>inet_pton4</c>: exactly four decimal octets, no leading zeros.</summary>
    static bool InetPton4(ReadOnlySpan<char> text, Span<byte> destination)
    {
        Span<byte> octets = stackalloc byte[4];
        octets.Clear();
        var sawDigit = false;
        var count = 0;
        var tp = 0;
        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9')
            {
                var next = (octets[tp] * 10) + (ch - '0');
                if (sawDigit && octets[tp] == 0)
                {
                    return false;
                }
                if (next > 255)
                {
                    return false;
                }
                octets[tp] = (byte)next;
                if (!sawDigit)
                {
                    if (++count > 4)
                    {
                        return false;
                    }
                    sawDigit = true;
                }
            }
            else if (ch == '.' && sawDigit)
            {
                if (count == 4)
                {
                    return false;
                }
                octets[++tp] = 0;
                sawDigit = false;
            }
            else
            {
                return false;
            }
        }
        if (count < 4)
        {
            return false;
        }
        octets.CopyTo(destination);
        return true;
    }
}
