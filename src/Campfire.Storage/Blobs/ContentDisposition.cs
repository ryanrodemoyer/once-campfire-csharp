using System.Text;

namespace Campfire.Storage.Blobs;

/// <summary>
/// <c>ActionDispatch::Http::ContentDisposition.format</c> (actionpack/lib/action_dispatch/http/
/// content_disposition.rb), as <c>send_file</c>, <c>send_data</c> and Active Storage name a file:
/// an ASCII <c>filename=</c> transliterated with I18n's default approximations, and the whole name
/// in RFC 5987's <c>filename*=</c>.
/// </summary>
public static class ContentDisposition
{
    /// <summary><c>format(disposition:, filename:)</c>; without a filename it is just the disposition.</summary>
    public static string Format(string disposition, string? filename) => filename is null
        ? disposition
        : $"{disposition}; filename=\"{PercentEscape(Transliterate(filename), IsTraditional)}\"; filename*=UTF-8''{PercentEscape(filename, IsRfc5987)}";

    /// <summary>
    /// <c>ActiveStorage::Service#content_disposition_with</c>: anything but <c>attachment</c> is
    /// <c>inline</c>, and the filename is sanitized.
    /// </summary>
    public static string For(string? disposition, Filename filename) =>
        Format(disposition == "attachment" ? "attachment" : "inline", filename.Sanitized);

    /// <summary>What <c>TRADITIONAL_ESCAPED_CHAR</c> leaves alone: <c>[ A-Za-z0-9!#$+.^_`|~-]</c>.</summary>
    static bool IsTraditional(char c) => c == ' ' || char.IsAsciiLetterOrDigit(c) || "!#$+.^_`|~-".Contains(c, StringComparison.Ordinal);

    /// <summary>What <c>RFC_5987_ESCAPED_CHAR</c> leaves alone: <c>[A-Za-z0-9!#$&amp;+.^_`|~-]</c>.</summary>
    static bool IsRfc5987(char c) => char.IsAsciiLetterOrDigit(c) || "!#$&+.^_`|~-".Contains(c, StringComparison.Ordinal);

    /// <summary>Each escaped character's UTF-8 bytes as <c>%XX</c>. Kept characters are all ASCII.</summary>
    static string PercentEscape(string value, Func<char, bool> keep)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b < 0x80 && keep((char)b))
            {
                escaped.Append((char)b);
            }
            else
            {
                escaped.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return escaped.ToString();
    }

    /// <summary>
    /// <c>I18n.transliterate</c> with the default approximations: each non-ASCII character (a whole
    /// code point, so an emoji is one) becomes its approximation, or <c>?</c>.
    /// </summary>
    static string Transliterate(string value)
    {
        var ascii = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                ascii.Append((char)rune.Value);
            }
            else if (rune.IsBmp && Approximations.Default.TryGetValue((char)rune.Value, out var approximation))
            {
                ascii.Append(approximation);
            }
            else
            {
                ascii.Append('?');
            }
        }
        return ascii.ToString();
    }
}
