using System.Text;

namespace Campfire.RailsCompat.UserAgent;

/// <summary>The Ruby and ActiveSupport string behavior the useragent gem relies on.</summary>
static class RubyText
{
    /// <summary><c>\s</c> in a Ruby regexp: ASCII whitespace only.</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary><c>String#strip</c>: ASCII whitespace and NUL.</summary>
    public static string Strip(string text) => text.Trim(StripChars);

    static readonly char[] StripChars = [' ', '\t', '\n', '\v', '\f', '\r', '\0'];

    /// <summary>ActiveSupport's <c>present?</c> for strings: not all Unicode whitespace.</summary>
    public static bool IsPresent(string? text) => text is not null && !text.All(char.IsWhiteSpace);

    /// <summary>
    /// <c>String#downcase</c>. Ruby maps U+0130 to "i̇" (full case mapping); .NET's invariant
    /// lowercasing is the simple mapping, which drops the combining dot.
    /// </summary>
    public static string Downcase(string text) => text.Replace("İ", "i̇", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary><c>a.downcase == b.downcase</c>.</summary>
    public static bool SameIgnoringCase(string a, string b) =>
        (Ascii.IsValid(a) && Ascii.IsValid(b)) ? a.Equals(b, StringComparison.OrdinalIgnoreCase) : Downcase(a) == Downcase(b);

    /// <summary><c>haystack.downcase.include?(needle)</c> for a lowercase ASCII needle.</summary>
    public static bool ContainsIgnoringCase(string haystack, string needle) =>
        Downcase(haystack).Contains(needle, StringComparison.Ordinal);

    /// <summary><c>String#split(separator)</c>: trailing empty fields are dropped.</summary>
    public static List<string> Split(string text, string separator)
    {
        var parts = text.Split(separator).ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }
        return parts;
    }
}
