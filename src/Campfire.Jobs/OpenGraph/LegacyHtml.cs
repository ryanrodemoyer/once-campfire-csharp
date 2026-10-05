using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Jobs.OpenGraph;

/// <summary>One <c>&lt;meta&gt;</c> element's attributes, lowercased names in document order.</summary>
public sealed class MetaElement(IReadOnlyList<KeyValuePair<string, string>> attributes)
{
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; } = attributes;

    public string? this[string name] => Attributes.FirstOrDefault(a => a.Key == name).Value;

    public bool HasAttribute(string name) => Attributes.Any(a => a.Key == name);
}

/// <summary>
/// The slice of <c>Nokogiri::HTML(html)</c> (libxml2's legacy HTML parser, in recovery mode) that
/// <c>Opengraph::Document</c> reads: every <c>&lt;meta&gt;</c> element's attributes.
/// <para>
/// The body string comes from <c>Opengraph::Fetch</c> tagged UTF-8, so libxml2 decodes it as UTF-8
/// whatever the page declares, taking a byte that isn't valid UTF-8 as Latin-1. Then it tokenizes
/// the way its pre-HTML5 parser does: <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> hold raw text,
/// comments and <c>&lt;!…&gt;</c>/<c>&lt;?…&gt;</c> markup are skipped, tag and attribute names
/// are lowercased, the first of a repeated attribute wins, and attribute values decode HTML 4
/// entities only when terminated by <c>;</c> and numeric references with or without one (an
/// invalid one cuts the value short, as the NUL it produces ends libxml2's C string).
/// </para>
/// Ported from the Rust port's opengraph/html.rs, which was probed against Nokogiri 1.19.4.
/// Everything the scanner matches is ASCII, so it only ever splits the text between characters.
/// </summary>
public static partial class LegacyHtml
{
    /// <summary>
    /// How many attributes of one tag are kept; the rest are parsed and dropped. Real tags have a
    /// handful, and a page with thousands in one tag gains nothing from them.
    /// </summary>
    const int maxAttributes = 256;

    /// <summary>libxml2 reading a UTF-8 buffer: valid sequences decode, any other byte is taken as Latin-1.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf8(bytes, out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
            {
                output.Append(rune.ToString());
                bytes = bytes[consumed..];
            }
            else
            {
                output.Append((char)bytes[0]);
                bytes = bytes[1..];
            }
        }
        return output.ToString();
    }

    /// <summary>The <c>&lt;meta&gt;</c> elements of the document, in document order.</summary>
    public static List<MetaElement> MetaElements(string html)
    {
        // A NUL ends libxml2's input
        var nul = html.IndexOf('\0', StringComparison.Ordinal);
        var scanner = new Scanner(nul < 0 ? html : html[..nul]);
        var metas = new List<MetaElement>();
        while (scanner.Peek(0) is { } c)
        {
            if (c != '<')
            {
                scanner.Position++;
                continue;
            }
            switch (scanner.Peek(1))
            {
                case '/':
                    scanner.EndTag();
                    break;
                case '!':
                    scanner.MarkupDeclaration();
                    break;
                case '?':
                    scanner.SkipPast('>');
                    break;
                case { } next when char.IsAsciiLetter(next):
                    var (name, element, selfClosing) = scanner.StartTag();
                    if (name == "meta")
                    {
                        metas.Add(element);
                    }
                    else if (name is "script" or "style" && !selfClosing)
                    {
                        scanner.RawText(name);
                    }
                    break;
                default:
                    scanner.Position++;
                    break;
            }
        }
        return metas;
    }

    /// <summary>
    /// <c>Nokogiri::HTML4::Document#meta_encoding</c>: the first <c>meta[@charset]</c>, else the
    /// charset in the first <c>http-equiv="Content-Type"</c> meta with a <c>content</c>.
    /// </summary>
    public static string? MetaEncoding(IReadOnlyList<MetaElement> metas)
    {
        if (metas.FirstOrDefault(m => m.HasAttribute("charset")) is { } withCharset)
        {
            return withCharset["charset"];
        }
        var meta = metas.FirstOrDefault(m =>
            m.HasAttribute("content") && m["http-equiv"] is { } httpEquiv && Ascii.EqualsIgnoreCase(httpEquiv, "content-type"));
        return meta is null ? null : CharsetIn(meta["content"]!);
    }

    /// <summary><c>content[/charset\s*=\s*([\w-]+)/i, 1]</c></summary>
    static string? CharsetIn(string content) =>
        CharsetRegex().Match(content) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"[Cc][Hh][Aa][Rr][Ss][Ee][Tt][ \t\n\x0B\x0C\r]*=[ \t\n\x0B\x0C\r]*([A-Za-z0-9_-]+)")]
    private static partial Regex CharsetRegex();

    static bool IsBlank(char c) => c is ' ' or '\t' or '\n' or '\r';

    sealed class Scanner(string text)
    {
        public int Position { get; set; }

        public char? Peek(int ahead) => Position + ahead < text.Length ? text[Position + ahead] : null;

        bool StartsWithIgnoreCase(string value) =>
            Position + value.Length <= text.Length && Ascii.EqualsIgnoreCase(text.AsSpan(Position, value.Length), value);

        string TextFrom(int start) => text[start..Position];

        void SkipBlanks()
        {
            while (Peek(0) is { } c && IsBlank(c))
            {
                Position++;
            }
        }

        /// <summary>Moves past the next <paramref name="c"/> (or to the end).</summary>
        public void SkipPast(char c)
        {
            while (Peek(0) is { } next)
            {
                Position++;
                if (next == c)
                {
                    break;
                }
            }
        }

        /// <summary><c>htmlParseHTMLName</c>: <c>[A-Za-z_:.][A-Za-z0-9:_.-]*</c>, lowercased.</summary>
        string? HtmlName()
        {
            if (Peek(0) is not { } first || !(char.IsAsciiLetter(first) || first is '_' or ':' or '.'))
            {
                return null;
            }
            var start = Position;
            while (Peek(0) is { } c && (char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.'))
            {
                Position++;
            }
            return TextFrom(start).ToLowerInvariant();
        }

        public void EndTag()
        {
            Position += 2;
            if (HtmlName() is not null)
            {
                SkipPast('>');
            }
        }

        /// <summary>
        /// <c>&lt;!--…--&gt;</c> (with <c>&lt;!--&gt;</c> and <c>&lt;!---&gt;</c> closing at once,
        /// and <c>--!&gt;</c> accepted), <c>&lt;!DOCTYPE…&gt;</c>, and any other <c>&lt;!…&gt;</c>
        /// skipped as a bogus comment.
        /// </summary>
        public void MarkupDeclaration()
        {
            if (Peek(2) != '-' || Peek(3) != '-')
            {
                SkipPast('>');
                return;
            }
            Position += 4;
            if (Peek(0) == '>')
            {
                Position++;
                return;
            }
            if (Peek(0) == '-' && Peek(1) == '>')
            {
                Position += 2;
                return;
            }
            while (Position < text.Length)
            {
                if (StartsWithIgnoreCase("-->"))
                {
                    Position += 3;
                    return;
                }
                if (StartsWithIgnoreCase("--!>"))
                {
                    Position += 4;
                    return;
                }
                Position++;
            }
        }

        /// <summary><c>htmlParseStartTag</c>: the name, the element, and whether it ended with <c>/&gt;</c>.</summary>
        public (string Name, MetaElement Element, bool SelfClosing) StartTag()
        {
            Position++;
            var name = HtmlName() ?? "";
            var attributes = new List<KeyValuePair<string, string>>();
            SkipBlanks();
            while (Peek(0) is { } c && c != '>' && !(c == '/' && Peek(1) == '>'))
            {
                if (HtmlName() is { } attribute)
                {
                    SkipBlanks();
                    var value = "";
                    if (Peek(0) == '=')
                    {
                        Position++;
                        SkipBlanks();
                        value = AttributeValue();
                    }
                    if (attributes.Count < maxAttributes && !attributes.Exists(a => a.Key == attribute))
                    {
                        attributes.Add(new(attribute, value));
                    }
                }
                else
                {
                    // Dump the bogus attribute string up to the next blank or the end of the tag
                    while (Peek(0) is { } b && !IsBlank(b) && b != '>' && !(b == '/' && Peek(1) == '>'))
                    {
                        Position++;
                    }
                }
                SkipBlanks();
            }
            var selfClosing = Peek(0) == '/';
            if (selfClosing)
            {
                Position += 2;
            }
            else if (Peek(0) == '>')
            {
                Position++;
            }
            return (name, new MetaElement(attributes), selfClosing);
        }

        /// <summary><c>htmlParseAttValue</c></summary>
        string AttributeValue()
        {
            if (Peek(0) is not ({ } quote and ('"' or '\'')))
            {
                return AttributeText(null);
            }
            Position++;
            var value = AttributeText(quote);
            if (Peek(0) == quote)
            {
                Position++;
            }
            return value;
        }

        /// <summary><c>htmlParseHTMLAttribute</c>: up to the quote, or (unquoted) a blank or <c>&gt;</c>.</summary>
        string AttributeText(char? stop)
        {
            bool EndsText(char c) => c == '&' || c == stop || (stop is null && (c == '>' || IsBlank(c)));

            var output = new StringBuilder();
            var truncated = false;
            while (true)
            {
                var start = Position;
                while (Peek(0) is { } c && !EndsText(c))
                {
                    Position++;
                }
                if (!truncated)
                {
                    output.Append(text, start, Position - start);
                }
                if (Peek(0) != '&')
                {
                    break;
                }
                var decoded = Peek(1) == '#' ? CharRef() : EntityRef();
                if (decoded is null)
                {
                    truncated = true;
                }
                else if (!truncated)
                {
                    output.Append(decoded);
                }
            }
            return output.ToString();
        }

        /// <summary><c>htmlParseCharRef</c>: null for a value that isn't a valid XML character.</summary>
        string? CharRef()
        {
            var hex = Peek(2) is 'x' or 'X';
            Position += hex ? 3 : 2;
            var radix = hex ? 16u : 10u;
            var value = 0u;
            while (Peek(0) is { } c)
            {
                if (c == ';')
                {
                    Position++;
                    break;
                }
                if (Digit(c, radix) is not { } digit)
                {
                    break;
                }
                if (value < 0x110000)
                {
                    value = value * radix + digit;
                }
                Position++;
            }
            var isChar = value is 0x9 or 0xA or 0xD or (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD) or (>= 0x10000 and <= 0x10FFFF);
            return isChar ? char.ConvertFromUtf32((int)value) : null;
        }

        static uint? Digit(char c, uint radix) => c switch
        {
            >= '0' and <= '9' => (uint)(c - '0'),
            >= 'a' and <= 'f' when radix == 16 => (uint)(c - 'a' + 10),
            >= 'A' and <= 'F' when radix == 16 => (uint)(c - 'A' + 10),
            _ => null,
        };

        /// <summary><c>htmlParseEntityRef</c>: a known name followed by <c>;</c> decodes; anything else stays as written.</summary>
        string EntityRef()
        {
            Position++;
            var start = Position;
            if (Peek(0) is { } first && (char.IsAsciiLetter(first) || first is '_' or ':'))
            {
                while (Peek(0) is { } c && (char.IsAsciiLetterOrDigit(c) || c is '_' or ':' or '.' or '-'))
                {
                    Position++;
                }
            }
            var name = TextFrom(start);
            if (name.Length > 0 && Peek(0) == ';' && Html4Entities.CodePoints.TryGetValue(name, out var codePoint))
            {
                Position++;
                return char.ConvertFromUtf32(codePoint);
            }
            return "&" + name;
        }

        /// <summary><c>htmlParseScript</c>: everything up to <c>&lt;/name</c> (any case) is text.</summary>
        public void RawText(string name)
        {
            var end = "</" + name;
            while (Position < text.Length && !StartsWithIgnoreCase(end))
            {
                Position++;
            }
        }
    }
}
