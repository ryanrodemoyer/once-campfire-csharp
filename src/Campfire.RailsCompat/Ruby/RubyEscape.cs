using System.Text;

namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// The escapers Rails, Rack and Addressable apply, byte for byte on the UTF-8 encoding.
/// </summary>
public static class RubyEscape
{
    static readonly string Hex = "0123456789ABCDEF";

    /// <summary><c>ERB::Util.html_escape</c> (and <c>h</c>): <c>&amp; &lt; &gt; " '</c> become <c>&amp;amp; &amp;lt; &amp;gt; &amp;quot; &amp;#39;</c>.</summary>
    public static string HtmlEscape(string s)
    {
        if (s.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return s;
        }
        var builder = new StringBuilder(s.Length + 16);
        AppendHtmlEscaped(builder, s);
        return builder.ToString();
    }

    /// <summary>Appends <see cref="HtmlEscape"/> of <paramref name="s"/>, a run of unescaped text at a time.</summary>
    public static void AppendHtmlEscaped(StringBuilder builder, ReadOnlySpan<char> s)
    {
        int index;
        while ((index = s.IndexOfAny("&<>\"'")) >= 0)
        {
            builder.Append(s[..index]);
            builder.Append(s[index] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => "&#39;",
            });
            s = s[(index + 1)..];
        }
        builder.Append(s);
    }

    /// <summary><c>CGI.escape</c>: everything but <c>A-Za-z0-9_.-~</c> is percent-encoded, a space as <c>+</c>.</summary>
    public static string CgiEscape(string s) => PercentEncode(s, spaceAsPlus: true, keep: "_.-~");

    /// <summary>
    /// <c>ERB::Util.url_encode</c>: everything but <c>A-Za-z0-9_.-~</c> is percent-encoded, a space as
    /// <c>%20</c>. Addressable's <c>encode_component(s, UNRESERVED)</c> keeps the same set.
    /// </summary>
    public static string UrlEncode(string s) => PercentEncode(s, spaceAsPlus: false, keep: "_.-~");

    /// <summary>
    /// <c>Rack::Utils.escape</c> (<c>URI.encode_www_form_component</c>): everything but
    /// <c>A-Za-z0-9*-._</c> is percent-encoded, a space as <c>+</c>. Cookie values are escaped this way.
    /// </summary>
    public static string RackEscape(string s) => PercentEncode(s, spaceAsPlus: true, keep: "*-._");

    static string PercentEncode(string s, bool spaceAsPlus, string keep)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || keep.Contains(c, StringComparison.Ordinal))
            {
                builder.Append(c);
            }
            else if (c == ' ' && spaceAsPlus)
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 0xf]);
            }
        }
        return builder.ToString();
    }
}
