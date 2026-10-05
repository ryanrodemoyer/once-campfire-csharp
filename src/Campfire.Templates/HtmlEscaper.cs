using System.Buffers;
using System.Text;

namespace Campfire.Templates;

// ERB::Util.html_escape (CGI.escapeHTML): the five characters below and nothing else.
// reference: activesupport/lib/active_support/core_ext/erb/util.rb (HTML_ESCAPE)
static class HtmlEscaper
{
    static readonly SearchValues<char> Special = SearchValues.Create("&<>\"'");

    public static string Escape(string text)
    {
        var next = text.AsSpan().IndexOfAny(Special);
        if (next < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        var rest = text.AsSpan();
        while (next >= 0)
        {
            builder.Append(rest[..next]).Append(Entity(rest[next]));
            rest = rest[(next + 1)..];
            next = rest.IndexOfAny(Special);
        }
        return builder.Append(rest).ToString();
    }

    public static void WriteEscaped(IBufferWriter<byte> output, ReadOnlySpan<char> text)
    {
        var next = text.IndexOfAny(Special);
        while (next >= 0)
        {
            WriteUtf8(output, text[..next]);
            output.Write(EntityUtf8(text[next]));
            text = text[(next + 1)..];
            next = text.IndexOfAny(Special);
        }
        WriteUtf8(output, text);
    }

    public static void WriteUtf8(IBufferWriter<byte> output, ReadOnlySpan<char> text)
    {
        const int ChunkChars = 4096;
        while (!text.IsEmpty)
        {
            var length = Math.Min(text.Length, ChunkChars);
            // Never split a surrogate pair across chunks.
            if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
            {
                length--;
            }
            var chunk = text[..length];
            var span = output.GetSpan(Encoding.UTF8.GetMaxByteCount(chunk.Length));
            output.Advance(Encoding.UTF8.GetBytes(chunk, span));
            text = text[length..];
        }
    }

    static string Entity(char c) => c switch
    {
        '&' => "&amp;",
        '<' => "&lt;",
        '>' => "&gt;",
        '"' => "&quot;",
        _ => "&#39;",
    };

    static ReadOnlySpan<byte> EntityUtf8(char c) => c switch
    {
        '&' => "&amp;"u8,
        '<' => "&lt;"u8,
        '>' => "&gt;"u8,
        '"' => "&quot;"u8,
        _ => "&#39;"u8,
    };
}
