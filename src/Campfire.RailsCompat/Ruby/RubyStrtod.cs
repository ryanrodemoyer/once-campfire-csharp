using System.Globalization;
using System.Text;

namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// <c>ruby_strtod</c> (David Gay's, in Ruby's missing/dtoa.c) and the buffer <c>rb_cstr_to_dbl</c>
/// copies a number into when it has underscores, over a string's UTF-8 bytes.
/// </summary>
static class RubyStrtod
{
    /// <summary>
    /// A number's significand fills at most this much of <c>rb_cstr_to_dbl</c>'s buffer
    /// (<c>DBL_DIG * 4</c>), and it all at most this much (<c>sizeof(buf) - 1</c>).
    /// </summary>
    static readonly int SignificandWidth = 60;
    static readonly int BufferWidth = 69;

    /// <summary>
    /// The most significant digits <c>ruby_strtod</c> reads of a fraction (<c>DBL_DIG * 4</c>): past
    /// them the rest is ignored. Zeros count once a digit follows them.
    /// </summary>
    static readonly int FractionDigits = 60;

    static byte At(byte[] s, int i) => i < s.Length ? s[i] : (byte)0;

    static bool IsDigit(byte b, int numberBase) => numberBase == 16 ? char.IsAsciiHexDigit((char)b) : char.IsAsciiDigit((char)b);

    static bool IsSpace(byte b) => RubyString.IsSpace((char)b);

    static int? HexDigit(byte b) => char.IsAsciiHexDigit((char)b) ? Convert.ToInt32(((char)b).ToString(), 16) : null;

    /// <summary>
    /// What <c>rb_cstr_to_dbl</c> hands <c>strtod</c> a second time when the first read stopped short
    /// at <paramref name="end"/>: the number copied into a fixed buffer with the underscores between
    /// digits left out, up to anything else that can't continue it. Significand characters past the
    /// buffer's first 60 are dropped, not scaled, so <c>"1" + "0" * 70 + "x"</c> is 1e59.
    /// </summary>
    public static byte[] WithoutUnderscores(byte[] s, int end)
    {
        var output = new List<byte>(BufferWidth);
        var width = SignificandWidth;
        var (numberBase, exponentLetter) = (10, (byte)'e');
        var dotSeen = false;
        byte previous = 0;
        var p = 0;
        if (At(s, p) is (byte)'+' or (byte)'-')
        {
            previous = At(s, p);
            output.Add(previous);
            p++;
        }
        if (At(s, p) == '0')
        {
            previous = (byte)'0';
            output.Add(previous);
            p++;
            if (At(s, p) is (byte)'x' or (byte)'X')
            {
                previous = (byte)'x';
                output.Add(previous);
                (numberBase, exponentLetter) = (16, (byte)'p');
                p++;
            }
            // Successive zeros are squeezed into the one.
            while (At(s, p) == '0')
            {
                p++;
            }
        }
        while (p < end && output.Count < width)
        {
            previous = s[p];
            output.Add(previous);
            p++;
        }
        while (p < s.Length)
        {
            if (s[p] == '_')
            {
                p++;
                if (output.Count == 0 || !IsDigit(previous, numberBase) || !IsDigit(At(s, p), numberBase))
                {
                    break;
                }
            }
            previous = s[p];
            p++;
            if (width == SignificandWidth && char.ToLowerInvariant((char)previous) == exponentLetter)
            {
                width = BufferWidth;
                output.Add(previous);
                if (At(s, p) is (byte)'+' or (byte)'-')
                {
                    previous = At(s, p);
                    output.Add(previous);
                    p++;
                }
                if (At(s, p) == '0')
                {
                    previous = (byte)'0';
                    output.Add(previous);
                    while (At(s, p) == '0')
                    {
                        p++;
                    }
                }
                numberBase = 10;
                continue;
            }
            else if (IsSpace(previous))
            {
                while (IsSpace(At(s, p)))
                {
                    p++;
                }
                if (p < s.Length)
                {
                    break;
                }
            }
            else if (previous == '.')
            {
                if (dotSeen)
                {
                    break;
                }
                dotSeen = true;
            }
            else if (!IsDigit(previous, numberBase))
            {
                break;
            }
            if (output.Count < width)
            {
                output.Add(previous);
            }
        }
        return [.. output];
    }

    /// <summary>
    /// The number at the start of <paramref name="s"/>, and how much of it the number takes up (0 for
    /// none). .NET parses the digits it reads; both round correctly.
    /// </summary>
    public static (double Value, int End) Parse(byte[] s)
    {
        var i = 0;
        while (IsSpace(At(s, i)))
        {
            i++;
        }
        var negative = At(s, i) == '-';
        if (At(s, i) is (byte)'+' or (byte)'-')
        {
            i++;
        }
        if (i == s.Length)
        {
            return (0.0, 0);
        }
        if (At(s, i) == '0' && At(s, i + 1) is (byte)'x' or (byte)'X')
        {
            return ParseHex(s, i + 2, negative);
        }
        var signedZero = negative ? -0.0 : 0.0;

        var leadingZero = At(s, i) == '0';
        while (At(s, i) == '0')
        {
            i++;
        }
        if (leadingZero && i == s.Length)
        {
            return (signedZero, i);
        }
        var integerStart = i;
        while (char.IsAsciiDigit((char)At(s, i)))
        {
            i++;
        }
        var integer = Encoding.ASCII.GetString(s, integerStart, i - integerStart);
        var digits = integer.Length;
        var fraction = new StringBuilder();
        var fractionZeros = false;
        if (At(s, i) == '.' && char.IsAsciiDigit((char)At(s, i + 1)))
        {
            i++;
            var zeros = 0;
            while (char.IsAsciiDigit((char)At(s, i)))
            {
                var digit = (char)At(s, i);
                i++;
                if (digits > FractionDigits)
                {
                    continue;
                }
                if (digit == '0')
                {
                    zeros++;
                    fractionZeros = true;
                    continue;
                }
                fraction.Append('0', zeros).Append(digit);
                digits += digits == 0 ? 1 : zeros + 1;
                zeros = 0;
            }
        }
        else if (At(s, i) == '.')
        {
            i++;
        }
        var anyDigits = digits > 0 || fractionZeros || leadingZero;

        var exponent = 0;
        if (At(s, i) is (byte)'e' or (byte)'E')
        {
            if (!anyDigits)
            {
                return (0.0, 0);
            }
            (exponent, i) = ParseExponent(s, i);
        }
        if (digits == 0)
        {
            return anyDigits ? (signedZero, i) : (0.0, 0);
        }
        var number = string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : "")}{integer}.{fraction}e{exponent}");
        return (double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture), i);
    }

    /// <summary>
    /// The exponent at <c>s[e]</c> (the <c>e</c>), and where it ends; <c>s[e]</c> itself when no digit
    /// follows. Past 19999 it's 19999, as in Ruby.
    /// </summary>
    static (int Exponent, int End) ParseExponent(byte[] s, int e)
    {
        var i = e + 1;
        var negative = At(s, i) == '-';
        if (At(s, i) is (byte)'+' or (byte)'-')
        {
            i++;
        }
        if (!char.IsAsciiDigit((char)At(s, i)))
        {
            return (0, e);
        }
        while (At(s, i) == '0')
        {
            i++;
        }
        var start = i;
        long exponent = 0;
        while (char.IsAsciiDigit((char)At(s, i)))
        {
            exponent = Math.Min(exponent * 10 + (At(s, i) - '0'), int.MaxValue);
            i++;
        }
        var value = i - start > 8 || exponent > 19999 ? 19999 : (int)exponent;
        return (negative ? -value : value, i);
    }

    /// <summary>
    /// <c>ruby_strtod</c>'s hexadecimal branch, from the digits after <c>0x</c> at
    /// <paramref name="start"/>: hex digits, a fraction and a binary exponent (<c>p</c>), added up as
    /// doubles the way Ruby does, then scaled.
    /// </summary>
    static (double Value, int End) ParseHex(byte[] s, int start, bool negative)
    {
        double Signed(double value) => negative ? -value : value;
        var i = start;
        if (HexDigit(At(s, i)) is null && At(s, i) != '.')
        {
            return (0.0, 0);
        }
        var (sum, weight, exponent) = (0.0, 1.0, -4L);
        while (At(s, i) == '0')
        {
            i++;
        }
        if (i == s.Length)
        {
            return (Signed(0.0), i);
        }
        while (HexDigit(At(s, i)) is { } digit)
        {
            sum += weight * digit;
            exponent += 4;
            weight /= 16.0;
            i++;
        }
        if (At(s, i) == '.')
        {
            i++;
            if (HexDigit(At(s, i)) is not null)
            {
                if (exponent < 0)
                {
                    while (At(s, i) == '0')
                    {
                        i++;
                        exponent -= 4;
                    }
                }
                while (HexDigit(At(s, i)) is { } digit)
                {
                    sum += weight * digit;
                    i++;
                    weight /= 16.0;
                    if (weight == 0.0)
                    {
                        while (HexDigit(At(s, i)) is not null)
                        {
                            i++;
                        }
                        break;
                    }
                }
            }
        }
        if (At(s, i) is (byte)'p' or (byte)'P')
        {
            i++;
            var sign = At(s, i) switch
            {
                (byte)'-' => -1,
                (byte)'+' => 1,
                _ => 0,
            };
            if (sign != 0)
            {
                i++;
            }
            sign = sign == 0 ? 1 : sign;
            if (!char.IsAsciiDigit((char)At(s, i)))
            {
                return (0.0, 0);
            }
            long power = 0;
            while (char.IsAsciiDigit((char)At(s, i)))
            {
                power = power * 10 + (At(s, i) - '0');
                i++;
                // Ruby stops reading the exponent past this, where any significand overflows.
                if (power + sign * exponent > 2095)
                {
                    while (char.IsAsciiDigit((char)At(s, i)))
                    {
                        i++;
                    }
                    break;
                }
            }
            exponent += power * sign;
        }
        return (Signed(ScaleByTwo(sum, exponent)), i);
    }

    /// <summary><c>ldexp(value, exponent)</c>: <paramref name="value"/> times 2 to the <paramref name="exponent"/>, rounded once (musl's <c>scalbn</c>).</summary>
    static double ScaleByTwo(double value, long exponent)
    {
        static double PowerOfTwo(long n) => BitConverter.Int64BitsToDouble((0x3ff + n) << 52);
        var n = exponent;
        if (n > 1023)
        {
            value *= PowerOfTwo(1023);
            n -= 1023;
            if (n > 1023)
            {
                value *= PowerOfTwo(1023);
                n = Math.Min(n - 1023, 1023);
            }
        }
        else if (n < -1022)
        {
            // Scaled so that the last step, into the subnormals, is the only one that rounds.
            value *= PowerOfTwo(-1022) * PowerOfTwo(53);
            n += 1022 - 53;
            if (n < -1022)
            {
                value *= PowerOfTwo(-1022) * PowerOfTwo(53);
                n = Math.Max(n + 1022 - 53, -1022);
            }
        }
        return value * PowerOfTwo(n);
    }
}
