using System.Buffers;
using System.Runtime.CompilerServices;
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
    public static string CgiEscape(string s) => PercentEncode(s, spaceAsPlus: true, UrlSafe);

    /// <summary>
    /// <c>ERB::Util.url_encode</c>: everything but <c>A-Za-z0-9_.-~</c> is percent-encoded, a space as
    /// <c>%20</c>. Addressable's <c>encode_component(s, UNRESERVED)</c> keeps the same set.
    /// </summary>
    public static string UrlEncode(string s) => PercentEncode(s, spaceAsPlus: false, UrlSafe);

    /// <summary>
    /// <c>Rack::Utils.escape</c> (<c>URI.encode_www_form_component</c>): everything but
    /// <c>A-Za-z0-9*-._</c> is percent-encoded, a space as <c>+</c>. Cookie values are escaped this way.
    /// </summary>
    public static string RackEscape(string s) => PercentEncode(s, spaceAsPlus: true, RackSafe);

    /// <summary>Appends <see cref="RackEscape"/> of <paramref name="s"/> to <paramref name="handler"/>, without a string of its own.</summary>
    internal static void AppendRackEscaped(ref DefaultInterpolatedStringHandler handler, string s)
    {
        if (s.AsSpan().IndexOfAnyExcept(RackSafe) < 0)
        {
            handler.AppendFormatted(s);
            return;
        }
        AppendPercentEncoded(ref handler, s, spaceAsPlus: true, RackSafe);
    }

    // The bytes that are kept as they are: ASCII letters and digits and these.
    static readonly SearchValues<char> UrlSafe = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-~");
    static readonly SearchValues<char> RackSafe = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789*-._");

    const int stackBytes = 256;
    const int stackChars = 1024;
    const int stageChars = 192;

    static string PercentEncode(string s, bool spaceAsPlus, SearchValues<char> safe)
    {
        // Nothing needs escaping: the text is the string that came in.
        if (s.AsSpan().IndexOfAnyExcept(safe) < 0)
        {
            return s;
        }
        var handler = new DefaultInterpolatedStringHandler(0, 0, null, stackalloc char[stackChars]);
        AppendPercentEncoded(ref handler, s, spaceAsPlus, safe);
        return handler.ToStringAndClear();
    }

    static void AppendPercentEncoded(ref DefaultInterpolatedStringHandler handler, string s, bool spaceAsPlus, SearchValues<char> safe)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte[]? rented = null;
        Span<byte> bytes = maxBytes <= stackBytes ? stackalloc byte[stackBytes] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var count = Encoding.UTF8.GetBytes(s, bytes);
            // Escaped text is built a stage at a time: one append to the handler for every 190 characters or so.
            Span<char> stage = stackalloc char[stageChars];
            var length = 0;
            for (var i = 0; i < count; i++)
            {
                if (length > stageChars - 3)
                {
                    handler.AppendFormatted(stage[..length]);
                    length = 0;
                }
                var b = bytes[i];
                var c = (char)b;
                if (safe.Contains(c))
                {
                    stage[length++] = c;
                }
                else if (c == ' ' && spaceAsPlus)
                {
                    stage[length++] = '+';
                }
                else
                {
                    stage[length++] = '%';
                    stage[length++] = Hex[b >> 4];
                    stage[length++] = Hex[b & 0xf];
                }
            }
            handler.AppendFormatted(stage[..length]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
