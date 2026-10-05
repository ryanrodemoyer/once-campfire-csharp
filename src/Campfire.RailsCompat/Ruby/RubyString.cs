namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// Ruby's own string coercions as the app meets them: <c>String#to_i</c> and <c>#strip</c>.
/// Checked against Ruby 3.4 in <c>vectors/ruby_core.json</c>.
/// </summary>
public static class RubyString
{
    /// <summary>Ruby's <c>ISSPACE</c>: what <c>to_i</c> and <c>to_f</c> skip, and <c>\s</c> in a regexp.</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary><c>String#strip</c>: NUL and ASCII whitespace off both ends (not U+00A0 and the like).</summary>
    public static string Strip(string s) => s.AsSpan().Trim(" \t\n\v\f\r\0").ToString();

    /// <summary><c>String#to_i</c>, saturating at the long bounds where Ruby goes on to a Bignum.</summary>
    public static long ToI(string s) => (long)Int128.Clamp(ToInt128(s), long.MinValue, long.MaxValue);

    /// <summary><c>String#to_i</c>, or null where Ruby's answer doesn't fit in a long.</summary>
    public static long? ToIChecked(string s)
    {
        var value = ToInt128(s);
        return value < long.MinValue || value > long.MaxValue ? null : (long)value;
    }

    /// <summary>
    /// <c>String#to_i</c>: optional leading whitespace and sign, an optional <c>0d</c>, then digits (an
    /// underscore allowed between two). Saturating, and wide enough to tell every long, and every
    /// file size, from what's past it.
    /// </summary>
    internal static Int128 ToInt128(ReadOnlySpan<char> s)
    {
        s = s.TrimStart(" \t\n\v\f\r");
        var negative = false;
        if (s.Length > 0 && s[0] is '-' or '+')
        {
            negative = s[0] == '-';
            s = s[1..];
        }
        if (s.StartsWith("0d", StringComparison.Ordinal) || s.StartsWith("0D", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        Int128 number = 0;
        var previousDigit = false;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                number = number > (Int128.MaxValue - 9) / 10 ? Int128.MaxValue : number * 10 + (c - '0');
                previousDigit = true;
            }
            else if (c == '_' && previousDigit)
            {
                previousDigit = false;
            }
            else
            {
                break;
            }
        }
        return negative ? -number : number;
    }
}
