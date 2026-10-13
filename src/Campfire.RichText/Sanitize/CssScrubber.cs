using System.Globalization;
using System.Text;
using Campfire.RailsCompat.Ruby;

namespace Campfire.RichText.Sanitize;

/// <summary>
/// <c>Loofah::HTML5::Scrub.scrub_css</c> (loofah 2.25.2), which every sanitizer in the app runs on a
/// <c>style</c> attribute: the declarations Crass 1.0.7 parses (<c>Crass.parse_properties</c>), each
/// kept when its property is safelisted and its value has something left once unsafe parts are
/// dropped, written back as <c>name:value;</c>.
/// </summary>
public static partial class CssScrubber
{
    static readonly HashSet<string> AllowedProperties =
    [
        "align-content", "align-items", "align-self", "aspect-ratio", "azimuth", "background-color", "border-bottom-color",
        "border-collapse", "border-color", "border-left-color", "border-right-color", "border-top-color", "clear", "color",
        "cursor", "direction", "display", "elevation", "flex", "flex-basis", "flex-direction", "flex-flow", "flex-grow",
        "flex-shrink", "flex-wrap", "float", "font", "font-family", "font-size", "font-style", "font-variant", "font-weight",
        "height", "justify-content", "letter-spacing", "line-height", "list-style", "list-style-type", "max-height",
        "max-width", "min-height", "min-width", "order", "overflow", "overflow-x", "overflow-y", "page-break-after",
        "page-break-before", "page-break-inside", "pause", "pause-after", "pause-before", "pitch", "pitch-range", "richness",
        "speak", "speak-header", "speak-numeral", "speak-punctuation", "speech-rate", "stress", "text-align",
        "text-decoration", "text-indent", "unicode-bidi", "vertical-align", "voice-family", "volume", "white-space", "width",
    ];

    static readonly HashSet<string> AllowedSvgProperties =
    [
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-linecap", "stroke-linejoin", "stroke-opacity", "stroke-width",
    ];

    static readonly HashSet<string> ShorthandProperties = ["background", "border", "margin", "padding"];

    static readonly HashSet<string> AllowedKeywords =
    [
        "!important", "aliceblue", "antiquewhite", "aqua", "aquamarine", "auto", "azure", "beige", "bisque", "black",
        "blanchedalmond", "block", "blue", "blueviolet", "bold", "both", "bottom", "brown", "burlywood", "cadetblue", "center",
        "chartreuse", "chocolate", "collapse", "coral", "cornflowerblue", "cornsilk", "crimson", "cyan", "darkblue", "darkcyan",
        "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki", "darkmagenta", "darkolivegreen", "darkorange",
        "darkorchid", "darkred", "darksalmon", "darkseagreen", "darkslateblue", "darkslategray", "darkslategrey",
        "darkturquoise", "darkviolet", "dashed", "deeppink", "deepskyblue", "dimgray", "dimgrey", "dodgerblue", "dotted",
        "double", "firebrick", "floralwhite", "forestgreen", "fuchsia", "gainsboro", "ghostwhite", "gold", "goldenrod", "gray",
        "green", "greenyellow", "grey", "groove", "hidden", "honeydew", "hotpink", "indianred", "indigo", "inherit", "initial",
        "inset", "italic", "ivory", "khaki", "lavender", "lavenderblush", "lawngreen", "left", "lemonchiffon", "lightblue",
        "lightcoral", "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink", "lightsalmon",
        "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "lime",
        "limegreen", "linen", "magenta", "maroon", "medium", "mediumaquamarine", "mediumblue", "mediumorchid", "mediumpurple",
        "mediumseagreen", "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue",
        "mintcream", "mistyrose", "moccasin", "navajowhite", "navy", "none", "normal", "nowrap", "oldlace", "olive",
        "olivedrab", "orange", "orangered", "orchid", "outset", "palegoldenrod", "palegreen", "paleturquoise",
        "palevioletred", "papayawhip", "peachpuff", "peru", "pink", "plum", "pointer", "powderblue", "purple", "red", "revert",
        "ridge", "right", "rosybrown", "royalblue", "saddlebrown", "salmon", "sandybrown", "seagreen", "seashell", "separate",
        "sienna", "silver", "skyblue", "slateblue", "slategray", "slategrey", "snow", "solid", "springgreen", "steelblue",
        "tan", "teal", "thick", "thin", "thistle", "tomato", "top", "transparent", "turquoise", "underline", "unset", "violet",
        "wheat", "white", "whitesmoke", "yellow", "yellowgreen",
    ];

    // Loofah's list, plus the "var" Lexxy's engine appends (lexxy 0.9.24, lib/lexxy/engine.rb)
    static readonly HashSet<string> AllowedFunctions =
    [
        "attr", "blur", "brightness", "calc", "circle", "contrast", "counter", "counters", "cubic-bezier", "drop-shadow",
        "ellipse", "grayscale", "hsl", "hsla", "hue-rotate", "hwb", "inset", "invert", "linear-gradient", "matrix", "matrix3d",
        "opacity", "perspective", "polygon", "radial-gradient", "repeating-linear-gradient", "repeating-radial-gradient", "rgb",
        "rgba", "rotate", "rotate3d", "rotateX", "rotateY", "rotateZ", "rotatex", "rotatey", "rotatez", "saturate", "scale",
        "scale3d", "scaleX", "scaleY", "scaleZ", "scalex", "scaley", "scalez", "sepia", "skew", "skewX", "skewY", "skewx",
        "skewy", "symbols", "translate", "translate3d", "translateX", "translateY", "translateZ", "translatex", "translatey",
        "translatez", "var",
    ];

    /// <summary><c>scrub_css(style)</c></summary>
    public static string Scrub(string style)
    {
        var output = new StringBuilder();
        foreach (var property in new Parser(new Tokenizer(style).Tokenize()).ParseProperties())
        {
            if (property.Children.Any(child => child.Kind is Kind.Url or Kind.BadUrl))
            {
                continue;
            }

            var name = property.Name.ToLowerInvariant();
            var shorthand = ShorthandProperties.Contains(name.Split('-')[0]);
            if (!AllowedProperties.Contains(name) && !AllowedSvgProperties.Contains(name) && !shorthand)
            {
                continue;
            }

            var value = new StringBuilder();
            foreach (var child in property.Children)
            {
                value.Append(child.Kind switch
                {
                    Kind.Whitespace => " ",
                    Kind.String => QuotedWithoutEmbeddedQuotes(child.Raw!) ? child.Raw : null,
                    Kind.FunctionNode => AllowedFunctions.Contains(child.Name!.ToLowerInvariant()) ? child.Serialize() : null,
                    Kind.Ident => !shorthand || AllowedKeywords.Contains(child.Value!) || Keywordish(child.Value!) ? child.Value : null,
                    _ => child.Raw,
                });
            }

            var scrubbed = RubyString.Strip(value.ToString());
            if (scrubbed.Length == 0)
            {
                continue;
            }
            if (property.Important)
            {
                scrubbed += " !important";
            }
            // Crass.parse_properties("name:value").first, written back: the same text, unless
            // joining the parts made something new, such as a comment
            var reparsed = new Parser(new Tokenizer($"{name}:{scrubbed}").Tokenize()).ParseProperties().FirstOrDefault();
            output.Append(reparsed is null ? "" : string.Concat(reparsed.Tokens.Select(token => token.Serialize()))).Append(';');
        }
        return output.ToString();
    }

    // CSS_PROPERTY_STRING_WITHOUT_EMBEDDED_QUOTES: /\A(["'])?[^"']+\1\z/. Onigmo fails a backreference
    // to a group that didn't match, so the quotes are required.
    static bool QuotedWithoutEmbeddedQuotes(string raw) =>
        raw.Length >= 3 && raw[0] is '"' or '\'' && raw[^1] == raw[0] && raw[1..^1].All(c => c is not ('"' or '\''));

    // CSS_KEYWORDISH: /\A(#[0-9a-fA-F]+|rgb\(\d+%?,\d*%?,?\d*%?\)?|-?\d{0,3}\.?\d{0,10}(ch|cm|r?em|ex|in|lh|mm|pc|pt|px|Q|vmax|vmin|vw|vh|%|,|\))?)\z/
    static bool Keywordish(string keyword) => KeywordishRegex().IsMatch(keyword);

    [System.Text.RegularExpressions.GeneratedRegex(@"\A(#[0-9a-fA-F]+|rgb\([0-9]+%?,[0-9]*%?,?[0-9]*%?\)?|-?[0-9]{0,3}\.?[0-9]{0,10}(ch|cm|r?em|ex|in|lh|mm|pc|pt|px|Q|vmax|vmin|vw|vh|%|,|\))?)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex KeywordishRegex();

    enum Kind
    {
        Whitespace, String, BadString, Hash, Delim, SuffixMatch, LeftParen, RightParen, SubstringMatch, Comma, Cdc, Cdo,
        Colon, Semicolon, AtKeyword, LeftBracket, RightBracket, PrefixMatch, LeftBrace, RightBrace, UnicodeRange, DashMatch,
        Column, IncludeMatch, Number, Percentage, Dimension, Ident, Function, Url, BadUrl,

        // Parser nodes
        FunctionNode, SimpleBlock, Error, AtRule, Declaration,
    }

    /// <summary>A Crass token, or a node the parser builds from them.</summary>
    sealed class Node(Kind kind)
    {
        public Kind Kind { get; } = kind;

        /// <summary>A token's source text.</summary>
        public string? Raw { get; init; }

        /// <summary>An ident's, function's or at-keyword's name, escapes resolved.</summary>
        public string? Value { get; init; }

        /// <summary>A function node's name.</summary>
        public string? Name { get; init; }

        /// <summary>The tokens a function node or a discarded block spans, for writing it back.</summary>
        public List<Node> Tokens { get; } = [];

        /// <summary>A simple block's component values.</summary>
        public List<Node> Children { get; } = [];

        /// <summary>A simple block's opening and closing characters.</summary>
        public string Start { get; init; } = "";

        public string End { get; init; } = "";

        /// <summary><c>Crass::Parser.stringify</c></summary>
        public string Serialize() => Kind == Kind.SimpleBlock
            ? Start + string.Concat(Children.Select(child => child.Serialize())) + End
            : Raw ?? string.Concat(Tokens.Select(token => token.Serialize()));
    }

    /// <summary>A declaration: its value's component values, and every token it spans.</summary>
    sealed record Property(string Name, List<Node> Children, bool Important, List<Node> Tokens);

    /// <summary><c>Crass::Tokenizer</c> without comment or hack preservation, over code points.</summary>
    sealed class Tokenizer
    {
        readonly int[] input;
        int pos;
        int marker;
        int lastStep; // what Scanner#reconsume (StringScanner#unscan) takes back
        string? current;

        public Tokenizer(string style)
        {
            // preprocess: CRLF, CR and FF are newlines; NUL is U+FFFD
            var text = style.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\f', '\n').Replace('\0', '�');
            var codePoints = new List<int>(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                codePoints.Add(rune.Value);
            }
            input = [.. codePoints];
        }

        public List<Node> Tokenize()
        {
            var tokens = new List<Node>();
            while (Consume() is { } token)
            {
                tokens.Add(token);
            }
            return tokens;
        }

        bool Eos => pos >= input.Length;

        static string Text(int codePoint) => char.ConvertFromUtf32(codePoint);

        string ConsumeChar()
        {
            if (Eos)
            {
                return "";
            }
            lastStep = 1;
            current = Text(input[pos++]);
            return current;
        }

        string Peek(int length = 1)
        {
            var builder = new StringBuilder();
            for (var i = pos; i < input.Length && i < pos + length; i++)
            {
                builder.Append(Text(input[i]));
            }
            return builder.ToString();
        }

        int PeekAt(int offset) => pos + offset < input.Length ? input[pos + offset] : -1;

        void Reconsume()
        {
            pos -= lastStep;
            lastStep = 0;
        }

        // Scanner#scan with a pattern of one or more code points satisfying `matches`, at most `max`
        string? Scan(Func<int, bool> matches, int max = int.MaxValue)
        {
            var start = pos;
            while (pos < input.Length && pos - start < max && matches(input[pos]))
            {
                pos++;
            }
            if (pos == start)
            {
                return null;
            }
            lastStep = pos - start;
            current = Text(input[pos - 1]);
            return Slice(start, pos);
        }

        string Slice(int start, int end)
        {
            var builder = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                builder.Append(Text(input[i]));
            }
            return builder.ToString();
        }

        static bool IsWhitespace(int c) => c is '\n' or '\t' or ' ';

        static bool IsDigit(int c) => c is >= '0' and <= '9';

        static bool IsHex(int c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');

        static bool IsNameStart(int c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' or >= 0x80;

        static bool IsName(int c) => IsNameStart(c) || IsDigit(c) || c == '-';

        static bool IsNonPrintable(int c) => c is (>= 0 and <= 8) or 0x0B or (>= 0x0E and <= 0x1F) or 0x7F;

        static int First(string text) => text.Length == 0 ? -1 : char.ConvertToUtf32(text, 0);

        static string At(string text, int index)
        {
            var runes = text.EnumerateRunes().Skip(index);
            return runes.Any() ? runes.First().ToString() : "";
        }

        Node Token(Kind kind, string? value = null) => new(kind) { Raw = Slice(marker, pos), Value = value };

        Node? Consume()
        {
            while (true)
            {
                if (Eos)
                {
                    return null;
                }
                marker = pos;
                if (Peek(2) != "/*")
                {
                    break;
                }
                // consume_comments
                pos += 2;
                var close = -1;
                for (var i = pos; i + 1 < input.Length; i++)
                {
                    if (input[i] == '*' && input[i + 1] == '/')
                    {
                        close = i;
                        break;
                    }
                }
                pos = close < 0 ? input.Length : close + 2;
                lastStep = 0;
                current = pos > 0 ? Text(input[pos - 1]) : null;
            }

            if (Scan(IsWhitespace) is not null)
            {
                return Token(Kind.Whitespace);
            }

            var c = ConsumeChar();
            switch (c)
            {
                case "\"" or "'":
                    return ConsumeString();
                case "#":
                    if ((PeekAt(0) is var next && next >= 0 && IsName(next)) || ValidEscape(Peek(2)))
                    {
                        return Token(Kind.Hash, ConsumeName());
                    }
                    return Token(Kind.Delim, c);
                case "$":
                    return MatchOperator('=', Kind.SuffixMatch, c);
                case "(":
                    return Token(Kind.LeftParen);
                case ")":
                    return Token(Kind.RightParen);
                case "*":
                    return MatchOperator('=', Kind.SubstringMatch, c);
                case "+":
                    if (StartNumber())
                    {
                        Reconsume();
                        return ConsumeNumeric();
                    }
                    return Token(Kind.Delim, c);
                case ",":
                    return Token(Kind.Comma);
                case "-":
                    var nextTwo = Peek(2);
                    if (StartNumber(c + nextTwo))
                    {
                        Reconsume();
                        return ConsumeNumeric();
                    }
                    if (nextTwo == "->")
                    {
                        ConsumeChar();
                        ConsumeChar();
                        return Token(Kind.Cdc);
                    }
                    if (StartIdentifier(c + nextTwo))
                    {
                        Reconsume();
                        return ConsumeIdent();
                    }
                    return Token(Kind.Delim, c);
                case ".":
                    if (StartNumber())
                    {
                        Reconsume();
                        return ConsumeNumeric();
                    }
                    return Token(Kind.Delim, c);
                case ":":
                    return Token(Kind.Colon);
                case ";":
                    return Token(Kind.Semicolon);
                case "<":
                    if (Peek(3) == "!--")
                    {
                        ConsumeChar();
                        ConsumeChar();
                        ConsumeChar();
                        return Token(Kind.Cdo);
                    }
                    return Token(Kind.Delim, c);
                case "@":
                    if (StartIdentifier(Peek(3)))
                    {
                        return Token(Kind.AtKeyword, ConsumeName());
                    }
                    return Token(Kind.Delim, c);
                case "[":
                    return Token(Kind.LeftBracket);
                case "\\":
                    if (ValidEscape())
                    {
                        Reconsume();
                        return ConsumeIdent();
                    }
                    return Token(Kind.Delim, c);
                case "]":
                    return Token(Kind.RightBracket);
                case "^":
                    return MatchOperator('=', Kind.PrefixMatch, c);
                case "{":
                    return Token(Kind.LeftBrace);
                case "}":
                    return Token(Kind.RightBrace);
                case "U" or "u":
                    // RE_UNICODE_RANGE_START: /\+(?:[0-9A-Fa-f]|\?)/
                    if (PeekAt(0) == '+' && PeekAt(1) is var digit && (IsHex(digit) || digit == '?'))
                    {
                        ConsumeChar();
                        return ConsumeUnicodeRange();
                    }
                    Reconsume();
                    return ConsumeIdent();
                case "|":
                    if (PeekAt(0) == '=')
                    {
                        ConsumeChar();
                        return Token(Kind.DashMatch);
                    }
                    if (PeekAt(0) == '|')
                    {
                        ConsumeChar();
                        return Token(Kind.Column);
                    }
                    return Token(Kind.Delim, c);
                case "~":
                    return MatchOperator('=', Kind.IncludeMatch, c);
                default:
                    var code = First(c);
                    if (IsDigit(code))
                    {
                        Reconsume();
                        return ConsumeNumeric();
                    }
                    if (IsNameStart(code))
                    {
                        Reconsume();
                        return ConsumeIdent();
                    }
                    return Token(Kind.Delim, c);
            }
        }

        Node MatchOperator(char next, Kind kind, string delim)
        {
            if (PeekAt(0) == next)
            {
                ConsumeChar();
                return Token(kind);
            }
            return Token(Kind.Delim, delim);
        }

        // consume_bad_url
        void ConsumeBadUrl()
        {
            while (!Eos)
            {
                if (ValidEscape())
                {
                    ConsumeEscaped();
                }
                else if (ValidEscape(Peek(2)))
                {
                    ConsumeChar();
                    ConsumeEscaped();
                }
                else if (ConsumeChar() == ")")
                {
                    break;
                }
            }
        }

        string ConsumeEscaped()
        {
            if (Eos)
            {
                return "�";
            }
            if (Scan(IsHex, 6) is { } hex)
            {
                if (PeekAt(0) >= 0 && IsWhitespace(PeekAt(0)))
                {
                    ConsumeChar();
                }
                var codePoint = long.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return codePoint == 0 || codePoint is >= 0xD800 and <= 0xDFFF || codePoint > 0x10FFFF ? "�" : Text((int)codePoint);
            }
            return ConsumeChar();
        }

        Node ConsumeIdent()
        {
            var value = ConsumeName();
            if (PeekAt(0) != '(')
            {
                return Token(Kind.Ident, value);
            }
            ConsumeChar();
            if (!string.Equals(value, "url", StringComparison.OrdinalIgnoreCase))
            {
                return Token(Kind.Function, value);
            }
            // RE_WHITESPACE_ANCHORED over the next two characters
            while (PeekAt(0) >= 0 && IsWhitespace(PeekAt(0)) && (PeekAt(1) < 0 || IsWhitespace(PeekAt(1))))
            {
                ConsumeChar();
            }
            // RE_QUOTED_URL_START: /\A[\n\u0009 ]?["']/
            var first = PeekAt(0);
            if (first is '"' or '\'' || (first >= 0 && IsWhitespace(first) && PeekAt(1) is '"' or '\''))
            {
                return Token(Kind.Function, value);
            }
            return ConsumeUrl();
        }

        string ConsumeName()
        {
            var result = new StringBuilder();
            while (!Eos)
            {
                if (Scan(IsName) is { } match)
                {
                    result.Append(match);
                    continue;
                }
                ConsumeChar();
                if (ValidEscape())
                {
                    result.Append(ConsumeEscaped());
                }
                else
                {
                    Reconsume();
                    return result.ToString();
                }
            }
            return result.ToString();
        }

        Node ConsumeNumeric()
        {
            // consume_number
            if (PeekAt(0) is '+' or '-')
            {
                ConsumeChar();
            }
            Scan(IsDigit);
            if (PeekAt(0) == '.' && PeekAt(1) >= 0 && IsDigit(PeekAt(1)))
            {
                pos++;
                Scan(IsDigit);
            }
            var exponentDigit = PeekAt(1) is '+' or '-' ? 2 : 1;
            if (PeekAt(0) is 'E' or 'e' && PeekAt(exponentDigit) >= 0 && IsDigit(PeekAt(exponentDigit)))
            {
                pos += exponentDigit;
                Scan(IsDigit);
            }

            if (StartIdentifier(Peek(3)))
            {
                ConsumeName();
                return Token(Kind.Dimension);
            }
            if (PeekAt(0) == '%')
            {
                ConsumeChar();
                return Token(Kind.Percentage);
            }
            return Token(Kind.Number);
        }

        Node ConsumeString()
        {
            var ending = current;
            while (!Eos)
            {
                var c = ConsumeChar();
                if (c == ending)
                {
                    break;
                }
                if (c == "\n")
                {
                    Reconsume();
                    return Token(Kind.BadString);
                }
                if (c == "\\")
                {
                    if (Eos)
                    {
                        continue;
                    }
                    if (PeekAt(0) == '\n')
                    {
                        ConsumeChar();
                    }
                    else
                    {
                        ConsumeEscaped();
                    }
                }
            }
            return Token(Kind.String);
        }

        Node ConsumeUnicodeRange()
        {
            var value = Scan(IsHex, 6) ?? "";
            var length = value.Length;
            while (length < 6 && PeekAt(0) == '?')
            {
                ConsumeChar();
                length++;
                value += "?";
            }
            if (!value.Contains('?', StringComparison.Ordinal) && PeekAt(0) == '-' && PeekAt(1) >= 0 && IsHex(PeekAt(1)))
            {
                ConsumeChar();
                Scan(IsHex, 6);
            }
            return Token(Kind.UnicodeRange);
        }

        Node ConsumeUrl()
        {
            Scan(IsWhitespace);
            while (!Eos)
            {
                var c = ConsumeChar();
                var code = First(c);
                if (c == ")")
                {
                    break;
                }
                if (IsWhitespace(code))
                {
                    Scan(IsWhitespace);
                    if (Eos || PeekAt(0) == ')')
                    {
                        ConsumeChar();
                        break;
                    }
                    ConsumeBadUrl();
                    return Token(Kind.BadUrl);
                }
                if (c is "\"" or "'" or "(" || IsNonPrintable(code))
                {
                    ConsumeBadUrl();
                    return Token(Kind.BadUrl);
                }
                if (c == "\\")
                {
                    if (ValidEscape())
                    {
                        ConsumeEscaped();
                    }
                    else
                    {
                        ConsumeBadUrl();
                        return Token(Kind.BadUrl);
                    }
                }
            }
            return Token(Kind.Url);
        }

        bool StartIdentifier(string? text = null)
        {
            text ??= current + Peek(2);
            var first = At(text, 0);
            if (first == "-")
            {
                var next = At(text, 1);
                return next == "-" || (next.Length > 0 && IsNameStart(First(next))) || ValidEscape(Slice2(text, 1));
            }
            if (first.Length > 0 && IsNameStart(First(first)))
            {
                return true;
            }
            return first == "\\" && ValidEscape(Slice2(text, 0));
        }

        bool StartNumber(string? text = null)
        {
            text ??= current + Peek(2);
            var first = At(text, 0);
            var second = At(text, 1);
            bool Digit(string s) => s.Length > 0 && IsDigit(First(s));
            return first switch
            {
                "+" or "-" => Digit(second) || (second == "." && Digit(At(text, 2))),
                "." => Digit(second),
                _ => Digit(first),
            };
        }

        static string Slice2(string text, int start) => At(text, start) + At(text, start + 1);

        bool ValidEscape(string? text = null)
        {
            text ??= current + Peek();
            return At(text, 0) == "\\" && At(text, 1) != "\n";
        }
    }

    /// <summary>The parts of <c>Crass::Parser</c> that <c>parse_properties</c> runs.</summary>
    sealed class Parser(List<Node> tokens)
    {
        // DEFAULT_MAXIMUM_DEPTH
        const int maximumDepth = 25;

        int depth;

        public List<Property> ParseProperties()
        {
            var properties = new List<Property>();
            var input = new TokenScanner(tokens);
            while (input.Consume() is { } token)
            {
                switch (token.Kind)
                {
                    case Kind.Whitespace or Kind.Semicolon:
                        break;
                    case Kind.AtKeyword:
                        input.Reconsume();
                        ConsumeAtRule(input);
                        break;
                    case Kind.Ident:
                        var declarationTokens = new List<Node> { token };
                        while (input.Peek() is { } next && next.Kind != Kind.Semicolon)
                        {
                            declarationTokens.Add(ConsumeComponentValue(input)!);
                        }
                        if (ConsumeDeclaration(new TokenScanner(declarationTokens)) is { } property)
                        {
                            properties.Add(property);
                        }
                        break;
                    default:
                        input.Reconsume();
                        while (input.Peek() is { } next && next.Kind != Kind.Semicolon)
                        {
                            ConsumeComponentValue(input);
                        }
                        break;
                }
            }
            return properties;
        }

        void ConsumeAtRule(TokenScanner input)
        {
            input.Consume();
            while (input.Consume() is { } token)
            {
                if (token.Kind == Kind.Semicolon)
                {
                    break;
                }
                if (token.Kind == Kind.LeftBrace)
                {
                    ConsumeSimpleBlock(input);
                    break;
                }
                input.Reconsume();
                ConsumeComponentValue(input);
            }
        }

        Node? ConsumeComponentValue(TokenScanner input)
        {
            if (input.Consume() is not { } token)
            {
                return null;
            }
            return token.Kind switch
            {
                Kind.LeftBrace or Kind.LeftBracket or Kind.LeftParen => ConsumeSimpleBlock(input),
                Kind.Function => ConsumeFunction(input),
                _ => token,
            };
        }

        // consume_declaration, over component values already grouped
        static Property? ConsumeDeclaration(TokenScanner input)
        {
            var name = input.Consume()!.Value!;
            while (input.Peek() is { Kind: Kind.Whitespace })
            {
                input.Consume();
            }
            if (input.Peek() is not { Kind: Kind.Colon })
            {
                return null;
            }
            input.Consume();
            var value = new List<Node>();
            while (input.Consume() is { } node)
            {
                value.Add(node);
            }

            var significant = value.Where(node => node.Kind is not (Kind.Whitespace or Kind.Semicolon)).TakeLast(2).ToList();
            var important = significant.Count == 2
                && significant[0] is { Kind: Kind.Delim, Value: "!" }
                && significant[1].Kind == Kind.Ident && string.Equals(significant[1].Value, "important", StringComparison.OrdinalIgnoreCase);
            if (important)
            {
                value.RemoveRange(value.IndexOf(significant[0]), value.Count - value.IndexOf(significant[0]));
            }
            if (value.Count > 0 && value[^1].Kind == Kind.Semicolon)
            {
                value.RemoveAt(value.Count - 1);
            }
            return new Property(name, value, important, input.All);
        }

        Node ConsumeFunction(TokenScanner input)
        {
            depth++;
            try
            {
                if (depth > maximumDepth)
                {
                    return DiscardBlock(input);
                }
                var function = new Node(Kind.FunctionNode) { Name = input.Current!.Value };
                function.Tokens.Add(input.Current!);
                var start = input.Position;
                while (input.Consume() is { } token)
                {
                    if (token.Kind == Kind.RightParen)
                    {
                        break;
                    }
                    input.Reconsume();
                    ConsumeComponentValue(input);
                }
                function.Tokens.AddRange(input.Slice(start));
                return function;
            }
            finally
            {
                depth--;
            }
        }

        Node ConsumeSimpleBlock(TokenScanner input)
        {
            depth++;
            try
            {
                if (depth > maximumDepth)
                {
                    return DiscardBlock(input);
                }
                var startKind = input.Current!.Kind;
                var end = EndOf(startKind);
                var block = new Node(Kind.SimpleBlock) { Start = input.Current.Raw!, End = Closer(end) };
                while (input.Consume() is { } token)
                {
                    if (token.Kind == end)
                    {
                        break;
                    }
                    input.Reconsume();
                    block.Children.Add(ConsumeComponentValue(input)!);
                }
                return block;
            }
            finally
            {
                depth--;
            }
        }

        static Node DiscardBlock(TokenScanner input)
        {
            var discarded = new Node(Kind.Error);
            discarded.Tokens.Add(input.Current!);
            var start = input.Position;
            var stack = new Stack<Kind>();
            stack.Push(input.Current!.Kind == Kind.Function ? Kind.RightParen : EndOf(input.Current!.Kind));
            while (stack.Count > 0 && input.Consume() is { } token)
            {
                if (token.Kind is Kind.LeftBrace or Kind.LeftBracket or Kind.LeftParen)
                {
                    stack.Push(EndOf(token.Kind));
                }
                else if (token.Kind == Kind.Function)
                {
                    stack.Push(Kind.RightParen);
                }
                else if (token.Kind == stack.Peek())
                {
                    stack.Pop();
                }
            }
            discarded.Tokens.AddRange(input.Slice(start));
            return discarded;
        }

        static string Closer(Kind end) => end switch
        {
            Kind.RightBrace => "}",
            Kind.RightBracket => "]",
            _ => ")",
        };

        static Kind EndOf(Kind start) => start switch
        {
            Kind.LeftBrace => Kind.RightBrace,
            Kind.LeftBracket => Kind.RightBracket,
            _ => Kind.RightParen,
        };
    }

    /// <summary><c>Crass::TokenScanner</c></summary>
    sealed class TokenScanner(List<Node> tokens)
    {
        public int Position { get; private set; }

        public Node? Current { get; private set; }

        public Node? Consume()
        {
            Current = Position < tokens.Count ? tokens[Position] : null;
            if (Current is not null)
            {
                Position++;
            }
            return Current;
        }

        public Node? Peek() => Position < tokens.Count ? tokens[Position] : null;

        public void Reconsume()
        {
            if (Position > 0)
            {
                Position--;
            }
        }

        public List<Node> Slice(int start) => tokens.GetRange(start, Position - start);

        public List<Node> All => tokens;
    }
}
