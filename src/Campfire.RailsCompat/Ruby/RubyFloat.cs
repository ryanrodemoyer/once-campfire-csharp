using System.Globalization;
using System.Numerics;
using System.Text;

namespace Campfire.RailsCompat.Ruby;

/// <summary><c>String#to_f</c> and <c>Float#to_s</c> as Ruby 3.4 has them.</summary>
public static class RubyFloat
{
    /// <summary>
    /// <c>String#to_f</c> (<c>rb_cstr_to_dbl</c> in Ruby 3.4's object.c, which reads up to the first
    /// NUL): the number after any leading whitespace, and 0.0 when there's none. An underscore between
    /// two digits is skipped, so <c>"1_000.5"</c> is 1000.5; <c>"1.2.3"</c> is 1.2 and <c>"1e2"</c>
    /// 100.0. Hexadecimal is read only after a sign: <c>"-0x1A"</c> is -26.0, and <c>"0x1A"</c> 0.0.
    /// </summary>
    public static double ToF(string s)
    {
        var nul = s.IndexOf('\0', StringComparison.Ordinal);
        var text = (nul < 0 ? s.AsSpan() : s.AsSpan(0, nul)).TrimStart(" \t\n\v\f\r");
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        if (IsHex(bytes))
        {
            return 0.0;
        }
        var (value, end) = RubyStrtod.Parse(bytes);
        if (end == 0 || end == bytes.Length)
        {
            return value;
        }
        var number = RubyStrtod.WithoutUnderscores(bytes, end);
        return IsHex(number) ? 0.0 : RubyStrtod.Parse(number).Value;
    }

    static bool IsHex(byte[] s) => s.Length > 1 && s[0] == '0' && s[1] is (byte)'x' or (byte)'X';

    /// <summary>
    /// <c>Float#to_s</c>: plain decimals from 1e-4 up to (not including) 1e15, and above that while the
    /// shortest digits still reach past the decimal point (<c>1000000000000000.1</c>); the exponent
    /// form otherwise (<c>flo_to_s</c> in Ruby 3.4's numeric.c).
    /// </summary>
    public static string ToS(double f)
    {
        if (double.IsNaN(f))
        {
            return "NaN";
        }
        if (double.IsInfinity(f))
        {
            return f > 0 ? "Infinity" : "-Infinity";
        }
        if (f == 0)
        {
            return double.IsNegative(f) ? "-0.0" : "0.0";
        }

        var (digits, decpt) = ShortestDigits(Math.Abs(f));
        var sign = f < 0 ? "-" : "";
        if (decpt < -3 || (decpt > 15 && digits.Length <= decpt))
        {
            var rest = digits.Length > 1 ? digits[1..] : "0";
            var e = decpt - 1;
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{digits[0]}.{rest}e{(e < 0 ? '-' : '+')}{Math.Abs(e):00}");
        }
        if (decpt <= 0)
        {
            return $"{sign}0.{new string('0', -decpt)}{digits}";
        }
        if (decpt >= digits.Length)
        {
            return $"{sign}{digits}{new string('0', decpt - digits.Length)}.0";
        }
        return $"{sign}{digits[..decpt]}.{digits[decpt..]}";
    }

    /// <summary>
    /// The shortest digits that read back as <paramref name="magnitude"/>, and where the decimal point
    /// goes in them. When two such forms are equally close, Ruby's dtoa takes the even one and .NET
    /// the upper one: <c>667020902720176.25.to_s</c> is "667020902720176.2". Those ties take 16 or 17
    /// digits, so there the exact value rounded half to even decides, as long as it reads back.
    /// </summary>
    static (string Digits, int Decpt) ShortestDigits(double magnitude)
    {
        var shortest = Digits(magnitude.ToString("R", CultureInfo.InvariantCulture));
        if (!ReadsBack(shortest, magnitude))
        {
            // .NET's shortest form is one digit short at a few powers of two (2**-25 and 2**-958).
            var exact = ExactDigits(magnitude);
            for (var length = shortest.Digits.Length + 1; ; length++)
            {
                var rounded = RoundHalfEven(exact, length);
                if (ReadsBack(rounded, magnitude))
                {
                    return rounded;
                }
            }
        }
        if (shortest.Digits.Length >= 16 && (shortest.Digits[^1] - '0') % 2 == 1)
        {
            var even = RoundHalfEven(ExactDigits(magnitude), shortest.Digits.Length);
            if (ReadsBack(even, magnitude))
            {
                return even;
            }
        }
        return shortest;
    }

    static bool ReadsBack((string Digits, int Decpt) number, double magnitude) =>
        double.Parse($"0.{number.Digits}e{number.Decpt}", CultureInfo.InvariantCulture) == magnitude;

    /// <summary>The significant digits of a formatted number, and where its decimal point goes: "1.2345E+06" is ("12345", 7).</summary>
    static (string Digits, int Decpt) Digits(string formatted)
    {
        var e = formatted.IndexOf('E', StringComparison.Ordinal);
        var exponent = e < 0 ? 0 : int.Parse(formatted.AsSpan(e + 1), CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? formatted : formatted[..e];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integer = dot < 0 ? mantissa : mantissa[..dot];
        var digits = (integer + (dot < 0 ? "" : mantissa[(dot + 1)..])).TrimEnd('0');
        var decpt = integer.Length + exponent;
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        return (digits[leadingZeros..], decpt - leadingZeros);
    }

    /// <summary>Every digit of a positive double's exact decimal value.</summary>
    static (string Digits, int Decpt) ExactDigits(double magnitude)
    {
        var bits = BitConverter.DoubleToInt64Bits(magnitude);
        var biased = (int)(bits >> 52) & 0x7ff;
        var mantissa = new BigInteger(bits & 0xf_ffff_ffff_ffffL);
        if (biased != 0)
        {
            mantissa += BigInteger.One << 52;
        }
        var exponent = (biased == 0 ? 1 : biased) - 1075;
        if (exponent >= 0)
        {
            var integer = (mantissa << exponent).ToString(CultureInfo.InvariantCulture);
            return (integer.TrimEnd('0'), integer.Length);
        }
        var scaled = (mantissa * BigInteger.Pow(5, -exponent)).ToString(CultureInfo.InvariantCulture);
        return (scaled.TrimEnd('0'), scaled.Length + exponent);
    }

    static (string Digits, int Decpt) RoundHalfEven((string Digits, int Decpt) exact, int length)
    {
        var (digits, decpt) = exact;
        if (digits.Length <= length)
        {
            return exact;
        }
        var head = digits[..length];
        var rest = digits[length..];
        var up = rest[0] > '5' || (rest[0] == '5' && (rest.AsSpan(1).ContainsAnyExcept('0') || (head[^1] - '0') % 2 == 1));
        if (!up)
        {
            return (head.TrimEnd('0'), decpt);
        }
        var rounded = (BigInteger.Parse(head, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        return rounded.Length > length ? (rounded[..length].TrimEnd('0'), decpt + 1) : (rounded.TrimEnd('0'), decpt);
    }
}
