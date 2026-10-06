using System.Text;

namespace Campfire.RichText.Attachments;

/// <summary>The Ruby and Active Support string behaviors attachment rendering depends on.</summary>
static class RubyText
{
    /// <summary>Active Support's <c>String#blank?</c>: empty or only Unicode whitespace.</summary>
    public static bool IsBlank(string? s)
    {
        if (s is null)
        {
            return true;
        }
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><c>Object#presence</c></summary>
    public static string? Presence(string? s) => IsBlank(s) ? null : s;

    /// <summary><c>String#chomp</c> with no argument: removes one trailing <c>\r\n</c>, <c>\n</c> or <c>\r</c>.</summary>
    public static string Chomp(string s)
    {
        if (s.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return s[..^2];
        }
        return s.EndsWith('\n') || s.EndsWith('\r') ? s[..^1] : s;
    }

    /// <summary>
    /// <c>String#truncate(length, omission:)</c> with no separator, counting characters as Ruby
    /// does (code points, not UTF-16 units).
    /// </summary>
    public static string Truncate(string text, int length, string omission)
    {
        var runes = text.EnumerateRunes().ToList();
        if (runes.Count <= length)
        {
            return text;
        }
        var keep = Math.Max(0, length - omission.EnumerateRunes().Count());
        var builder = new StringBuilder();
        foreach (var rune in runes.Take(keep))
        {
            builder.Append(rune.ToString());
        }
        return builder.Append(omission).ToString();
    }

    /// <summary>Whether <paramref name="classes"/> (an HTML class attribute) holds <paramref name="name"/>, as a CSS class selector matches it.</summary>
    public static bool HasClass(string? classes, string name) =>
        classes is not null && classes.Split([' ', '\t', '\n', '\r']).Contains(name, StringComparer.Ordinal);
}
