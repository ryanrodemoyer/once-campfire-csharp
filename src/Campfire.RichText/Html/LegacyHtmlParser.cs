using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.RichText.Html;

/// <summary>
/// <c>Nokogiri::HTML4::DocumentFragment.parse</c>, which <c>Nokogiri::HTML.fragment</c> calls:
/// libxml2's legacy HTML parser, in recovery mode, over <c>"&lt;html&gt;&lt;body&gt;" + input</c>,
/// keeping what lands in the body.
/// <para>
/// Ported from HTMLparser.c of libxml2 2.13.9, the version the reference's Nokogiri 1.19.4 bundles,
/// including how it recovers from broken markup: a start tag closes the open elements it can't
/// sit in (<c>htmlStartClose</c>), an end tag closes only elements of lower priority, an
/// unfinished start tag still makes its element, and a NUL ends the input wherever markup is
/// expected (inside text it reads as a space). Comments, processing instructions and doctypes are
/// parsed and dropped, since nothing that reads this tree sees them.
/// </para>
/// </summary>
public static partial class LegacyHtmlParser
{
    public static HtmlFragment ParseFragment(string input)
    {
        var parser = new Parser("<html><body>" + input);
        parser.ParseContent();

        // Nokogiri takes /html/body when the input starts a line with <body, else /html/body/node()
        var bodies = parser.Root.Elements.Where(e => e.Name == "html").SelectMany(html => html.Elements.Where(e => e.Name == "body")).ToList();
        List<HtmlNode> nodes = BodyLine().IsMatch(input) ? [.. bodies] : [.. bodies.SelectMany(body => body.Children)];
        var fragment = new HtmlFragment();
        foreach (var node in nodes)
        {
            fragment.AppendChild(node);
        }
        return fragment;
    }

    [GeneratedRegex(@"^[ \t\r\n\f\v]*?<body", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BodyLine();

    // Elements whose end tag is forbidden: they close as soon as they open
    static readonly HashSet<string> EmptyElements = ["area", "base", "basefont", "br", "col", "frame", "hr", "img", "input", "isindex", "link", "meta", "param"];

    static readonly HashSet<string> BooleanAttributes = ["checked", "compact", "declare", "defer", "disabled", "ismap", "multiple", "nohref", "noresize", "noshade", "nowrap", "readonly", "selected"];

    // htmlStartClose: an open element (the key) that a start tag (one of the values) closes
    static readonly Dictionary<string, HashSet<string>> StartClose = new()
    {
        ["a"] = ["a", "fieldset", "table", "td", "th"],
        ["address"] = ["dd", "dl", "dt", "form", "li", "ul"],
        ["b"] = ["center", "p", "td", "th"],
        ["big"] = ["p"],
        ["caption"] = ["col", "colgroup", "tbody", "tfoot", "thead", "tr"],
        ["col"] = ["col", "colgroup", "tbody", "tfoot", "thead", "tr"],
        ["colgroup"] = ["colgroup", "tbody", "tfoot", "thead", "tr"],
        ["dd"] = ["dt"],
        ["dir"] = ["dd", "dl", "dt", "form", "ul"],
        ["dl"] = ["form", "li"],
        ["dt"] = ["dd", "dl"],
        ["font"] = ["center", "td", "th"],
        ["form"] = ["form"],
        ["h1"] = ["fieldset", "form", "li", "p", "table"],
        ["h2"] = ["fieldset", "form", "li", "p", "table"],
        ["h3"] = ["fieldset", "form", "li", "p", "table"],
        ["h4"] = ["fieldset", "form", "li", "p", "table"],
        ["h5"] = ["fieldset", "form", "li", "p", "table"],
        ["h6"] = ["fieldset", "form", "li", "p", "table"],
        ["head"] = ["a", "abbr", "acronym", "address", "b", "bdo", "big", "blockquote", "body", "br", "center", "cite", "code", "dd", "dfn", "dir", "div", "dl", "dt", "em", "fieldset", "font", "form", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "iframe", "img", "kbd", "li", "listing", "map", "menu", "ol", "p", "pre", "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "table", "tt", "u", "ul", "var", "xmp"],
        ["hr"] = ["form"],
        ["i"] = ["center", "p", "td", "th"],
        ["legend"] = ["fieldset"],
        ["li"] = ["li"],
        ["link"] = ["body", "frameset"],
        ["listing"] = ["dd", "dl", "dt", "fieldset", "form", "li", "table", "ul"],
        ["menu"] = ["dd", "dl", "dt", "form", "ul"],
        ["ol"] = ["form"],
        ["option"] = ["optgroup", "option"],
        ["p"] = ["address", "blockquote", "body", "caption", "center", "col", "colgroup", "dd", "dir", "div", "dl", "dt", "fieldset", "form", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "hr", "li", "listing", "menu", "ol", "p", "pre", "table", "tbody", "td", "tfoot", "th", "title", "tr", "ul", "xmp"],
        ["pre"] = ["dd", "dl", "dt", "fieldset", "form", "li", "table", "ul"],
        ["s"] = ["p"],
        ["script"] = ["noscript"],
        ["small"] = ["p"],
        ["span"] = ["td", "th"],
        ["strike"] = ["p"],
        ["style"] = ["body", "frameset"],
        ["tbody"] = ["tbody", "tfoot"],
        ["td"] = ["tbody", "td", "tfoot", "th", "tr"],
        ["tfoot"] = ["tbody"],
        ["th"] = ["tbody", "td", "tfoot", "th", "tr"],
        ["thead"] = ["tbody", "tfoot"],
        ["title"] = ["body", "frameset"],
        ["tr"] = ["tbody", "tfoot", "tr"],
        ["tt"] = ["p"],
        ["u"] = ["p", "td", "th"],
        ["ul"] = ["address", "form", "menu", "pre"],
        ["xmp"] = ["dd", "dl", "dt", "fieldset", "form", "li", "table", "ul"],
    };

    // htmlEndPriority: an end tag closes open elements only up to its own priority
    static int EndPriority(string name) => name switch
    {
        "div" => 150,
        "td" or "th" => 160,
        "tr" => 170,
        "thead" or "tbody" or "tfoot" => 180,
        "table" => 190,
        "head" or "body" => 200,
        "html" => 220,
        _ => 100,
    };

    static bool ClosesOnStart(string newTag, string oldTag) => StartClose.TryGetValue(oldTag, out var closers) && closers.Contains(newTag);

    static bool IsBlank(int c) => c is ' ' or '\t' or '\n' or '\r';

    // IS_CHAR: what XML allows in text
    static bool IsChar(int c) => c is 0x9 or 0xA or 0xD or (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD) or (>= 0x10000 and <= 0x10FFFF);

    static char AsciiUpper(char c) => c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

    static int Utf8Length(int c) => c switch { < 0x80 => 1, < 0x800 => 2, < 0x10000 => 3, _ => 4 };

    static void AppendCodePoint(StringBuilder builder, int c)
    {
        if (c is >= 0xD800 and <= 0xDFFF)
        {
            builder.Append((char)c);
        }
        else
        {
            builder.Append(char.ConvertFromUtf32(c));
        }
    }

    sealed class Parser(string text)
    {
        // HTML_PARSER_BUFFER_SIZE: names are cut at this many characters
        const int maxNameLength = 100;

        // HTML_PARSER_BIG_BUFFER_SIZE: text is handed over in chunks of about this many bytes
        const int textChunk = 1000;

        readonly List<string> names = [];
        readonly List<HtmlParentNode> nodes = [];
        int position;

        // ctxt->html: 3 once a head was opened, 10 once a body was
        int seen = 1;

        // ctxt->depth: misplaced html, head and body start tags whose end tags are still to come
        int discarded;

        public HtmlFragment Root { get; } = new();

        string? Name => names.Count > 0 ? names[^1] : null;

        // CUR and NXT read raw characters, so a NUL anywhere reads as the end
        char Cur => Next(0);

        char Next(int n) => position + n < text.Length ? text[position + n] : '\0';

        /// <summary>CUR_CHAR: a whole character, with a NUL before the end read as a space.</summary>
        int CurrentChar(out int length)
        {
            if (position >= text.Length)
            {
                length = 0;
                return 0;
            }
            var c = text[position];
            if (c == '\0')
            {
                length = 1;
                return ' ';
            }
            if (char.IsHighSurrogate(c) && position + 1 < text.Length && char.IsLowSurrogate(text[position + 1]))
            {
                length = 2;
                return char.ConvertToUtf32(c, text[position + 1]);
            }
            length = 1;
            return c;
        }

        /// <summary>NEXT</summary>
        void Advance()
        {
            CurrentChar(out var length);
            position += length;
        }

        void SkipBlanks()
        {
            while (IsBlank(Cur))
            {
                position++;
            }
        }

        // htmlnamePush with the startElement that follows it
        void Push(string name, List<(string Name, string? Value)>? attributes = null)
        {
            if (seen < 3 && name == "head")
            {
                seen = 3;
            }
            if (seen < 10 && name == "body")
            {
                seen = 10;
            }
            names.Add(name);
            var element = new HtmlElement(name);
            foreach (var (attribute, value) in attributes ?? [])
            {
                element.AttributeList.Add(new HtmlAttr(attribute, value ?? (BooleanAttributes.Contains(attribute) ? attribute : "")));
            }
            (nodes.Count > 0 ? nodes[^1] : Root).AppendChild(element);
            nodes.Add(element);
        }

        // endElement with htmlnamePop
        void Pop()
        {
            names.RemoveAt(names.Count - 1);
            nodes.RemoveAt(nodes.Count - 1);
        }

        // xmlSAX2Characters: text outside any element is dropped
        void Characters(string value)
        {
            if (nodes.Count == 0)
            {
                return;
            }
            var parent = nodes[^1];
            if (parent.LastChild is HtmlText last)
            {
                last.Data += value;
            }
            else
            {
                parent.AppendChild(new HtmlText(value));
            }
        }

        /// <summary>htmlParseContentInternal, which is the document's whole content here.</summary>
        public void ParseContent()
        {
            var depth = names.Count;
            var currentNode = Name;
            while (true)
            {
                if (Cur == '<' && Next(1) == '/')
                {
                    if (ParseEndTag() && (currentNode is not null || names.Count == 0))
                    {
                        depth = names.Count;
                        currentNode = Name;
                    }
                    continue;
                }
                if (Cur == '<' && (char.IsAsciiLetter(Next(1)) || Next(1) is '_' or ':') && Name is { } open && ClosesOnStart(NameAhead(), open))
                {
                    AutoClose(NameAhead());
                    continue;
                }

                // The node was closed while parsing the next element
                if (names.Count > 0 && depth >= names.Count && currentNode != Name)
                {
                    FinishElement();
                    currentNode = Name;
                    depth = names.Count;
                    continue;
                }

                if (Cur != '\0' && currentNode is "script" or "style")
                {
                    ParseScript();
                }
                else if (Cur == '<' && Next(1) == '!')
                {
                    if (AsciiUpper(Next(2)) == 'D' && AsciiUpper(Next(3)) == 'O' && AsciiUpper(Next(4)) == 'C' && AsciiUpper(Next(5)) == 'T' &&
                        AsciiUpper(Next(6)) == 'Y' && AsciiUpper(Next(7)) == 'P' && AsciiUpper(Next(8)) == 'E')
                    {
                        ParseDoctype();
                    }
                    else if (Next(2) == '-' && Next(3) == '-')
                    {
                        ParseComment();
                    }
                    else
                    {
                        SkipBogusComment();
                    }
                }
                else if (Cur == '<' && Next(1) == '?')
                {
                    ParseProcessingInstruction();
                }
                else if (Cur == '<' && char.IsAsciiLetter(Next(1)))
                {
                    ParseElement();
                    currentNode = Name;
                    depth = names.Count;
                }
                else if (Cur == '<')
                {
                    Characters("<");
                    Advance();
                }
                else if (Cur == '&')
                {
                    ParseReference();
                }
                else if (Cur == '\0')
                {
                    AutoCloseOnEnd();
                    break;
                }
                else
                {
                    ParseCharData();
                }
            }
        }

        /// <summary>htmlParseElementInternal</summary>
        void ParseElement()
        {
            var failed = ParseStartTag();
            // A misplaced (discarded) tag carries on as the open element
            var name = Name;
            if (failed == -1 || name is null)
            {
                if (Cur == '>')
                {
                    Advance();
                }
                return;
            }
            if (Cur == '/' && Next(1) == '>')
            {
                position += 2;
                Pop();
                return;
            }
            if (Cur == '>')
            {
                Advance();
            }
            else
            {
                // Couldn't find the end of the start tag: the input ended
                Pop();
                FinishElement();
                return;
            }
            if (EmptyElements.Contains(name))
            {
                Pop();
            }
        }

        // htmlParserFinishElementParsing
        void FinishElement()
        {
            if (Cur == '\0')
            {
                AutoCloseOnEnd();
            }
        }

        /// <summary>htmlParseStartTag: -1 when it isn't one, 1 when it's discarded, else 0.</summary>
        int ParseStartTag()
        {
            if (Cur != '<')
            {
                return -1;
            }
            Advance();
            var name = ParseHtmlName();
            if (name is null)
            {
                while (Cur != '\0' && Cur != '>')
                {
                    Advance();
                }
                return -1;
            }

            AutoClose(name);
            CheckImplied(name);

            // No html below the top, head only at the second level, and no body inside a body
            var discard = false;
            if (names.Count > 0 && name == "html")
            {
                discard = true;
                discarded++;
            }
            if (names.Count != 1 && name == "head")
            {
                discard = true;
                discarded++;
            }
            if (name == "body")
            {
                foreach (var open in names.Where(open => open == "body"))
                {
                    discard = true;
                    discarded++;
                }
            }

            SkipBlanks();
            var attributes = new List<(string Name, string? Value)>();
            while (Cur != '\0' && Cur != '>' && !(Cur == '/' && Next(1) == '>'))
            {
                if (ParseAttribute(out var value) is { } attribute)
                {
                    // The first of a repeated attribute wins
                    if (!attributes.Exists(a => a.Name == attribute))
                    {
                        attributes.Add((attribute, value));
                    }
                }
                else
                {
                    // Dump the bogus attribute string up to the next blank or the end of the tag
                    while (Cur != '\0' && !IsBlank(Cur) && Cur != '>' && !(Cur == '/' && Next(1) == '>'))
                    {
                        Advance();
                    }
                }
                SkipBlanks();
            }

            if (!discard)
            {
                Push(name, attributes);
            }
            return discard ? 1 : 0;
        }

        /// <summary>htmlParseEndTag: whether it closed the open element.</summary>
        bool ParseEndTag()
        {
            position += 2;
            var name = ParseHtmlName();
            if (name is null)
            {
                return false;
            }
            SkipBlanks();
            while (Cur != '\0' && Cur != '>')
            {
                Advance();
            }
            if (Cur == '>')
            {
                Advance();
            }

            // The end of a discarded html, head or body start tag
            if (discarded > 0 && name is "html" or "body" or "head")
            {
                discarded--;
                return false;
            }
            if (!names.Contains(name))
            {
                return false;
            }

            AutoCloseOnClose(name);
            if (Name == name)
            {
                Pop();
                return true;
            }
            return false;
        }

        // htmlAutoClose
        void AutoClose(string newTag)
        {
            while (Name is { } open && ClosesOnStart(newTag, open))
            {
                Pop();
            }
        }

        // htmlAutoCloseOnClose: an end tag closes what's open above its element, unless something
        // of higher priority is in the way
        void AutoCloseOnClose(string newTag)
        {
            var priority = EndPriority(newTag);
            int i;
            for (i = names.Count - 1; i >= 0; i--)
            {
                if (names[i] == newTag)
                {
                    break;
                }
                if (EndPriority(names[i]) > priority)
                {
                    return;
                }
            }
            if (i < 0)
            {
                return;
            }
            while (Name != newTag)
            {
                Pop();
            }
        }

        void AutoCloseOnEnd()
        {
            while (names.Count > 0)
            {
                Pop();
            }
        }

        // htmlCheckImplied: the html, head and body a tag needs around it
        void CheckImplied(string newTag)
        {
            if (newTag == "html")
            {
                return;
            }
            if (names.Count == 0)
            {
                Push("html");
            }
            if (newTag is "body" or "head")
            {
                return;
            }
            if (names.Count <= 1 && newTag is "script" or "style" or "meta" or "link" or "title" or "base")
            {
                if (seen < 3)
                {
                    Push("head");
                }
            }
            else if (newTag is not ("noframes" or "frame" or "frameset"))
            {
                if (seen >= 10 || names.Exists(open => open is "body" or "head"))
                {
                    return;
                }
                Push("body");
            }
        }

        // htmlCheckParagraph: text straight inside html or head (or nothing) gets a p
        void CheckParagraph()
        {
            if (Name is null or "html" or "head")
            {
                AutoClose("p");
                CheckImplied("p");
                Push("p");
            }
        }

        // areBlanks, as far as it matters: to CheckParagraph, which only acts in html or head
        bool AreBlanks(string value) =>
            value.All(c => IsBlank(c)) && (Cur == '\0' || (Cur == '<' && Name is null or "html" or "head"));

        void FlushText(StringBuilder buffer)
        {
            var value = buffer.ToString();
            buffer.Clear();
            if (!AreBlanks(value))
            {
                CheckParagraph();
            }
            Characters(value);
        }

        /// <summary>htmlParseCharDataInternal: text up to the next <c>&lt;</c> or <c>&amp;</c>, without characters XML forbids.</summary>
        void ParseCharData()
        {
            var buffer = new StringBuilder();
            var bytes = 0;
            var c = CurrentChar(out var length);
            while (c is not ('<' or '&' or 0))
            {
                if (IsChar(c))
                {
                    AppendCodePoint(buffer, c);
                    bytes += Utf8Length(c);
                }
                position += length;
                if (bytes >= textChunk)
                {
                    FlushText(buffer);
                    bytes = 0;
                }
                c = CurrentChar(out length);
            }
            if (buffer.Length > 0)
            {
                FlushText(buffer);
            }
        }

        /// <summary>htmlParseScript: a script or style element's text, up to an end tag that starts with its name.</summary>
        void ParseScript()
        {
            var buffer = new StringBuilder();
            var bytes = 0;
            var c = CurrentChar(out var length);
            while (c != 0)
            {
                if (c == '<' && Next(1) == '/' && Name is { } name && position + 2 + name.Length <= text.Length &&
                    string.Compare(text, position + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    break;
                }
                if (IsChar(c))
                {
                    AppendCodePoint(buffer, c);
                    bytes += Utf8Length(c);
                }
                position += length;
                if (bytes >= textChunk)
                {
                    Characters(buffer.ToString());
                    buffer.Clear();
                    bytes = 0;
                }
                c = CurrentChar(out length);
            }
            if (buffer.Length > 0)
            {
                Characters(buffer.ToString());
            }
        }

        /// <summary>htmlParseReference: a character or entity reference in text.</summary>
        void ParseReference()
        {
            if (Next(1) == '#')
            {
                var c = ParseCharRef();
                if (c == 0)
                {
                    return;
                }
                CheckParagraph();
                Characters(char.ConvertFromUtf32(c));
                return;
            }
            var entity = ParseEntityRef(out var name);
            CheckParagraph();
            Characters(name is null ? "&" : entity is { } value ? char.ConvertFromUtf32(value) : "&" + name);
        }

        /// <summary>htmlParseCharRef: 0 when the value isn't a character XML allows.</summary>
        int ParseCharRef()
        {
            var value = 0;
            var hex = Next(2) is 'x' or 'X';
            position += hex ? 3 : 2;
            while (Cur != ';')
            {
                int digit;
                if (Cur is >= '0' and <= '9')
                {
                    digit = Cur - '0';
                }
                else if (hex && Cur is >= 'a' and <= 'f')
                {
                    digit = Cur - 'a' + 10;
                }
                else if (hex && Cur is >= 'A' and <= 'F')
                {
                    digit = Cur - 'A' + 10;
                }
                else
                {
                    break;
                }
                if (value < 0x110000)
                {
                    value = value * (hex ? 16 : 10) + digit;
                }
                Advance();
            }
            if (Cur == ';')
            {
                Advance();
            }
            return IsChar(value) ? value : 0;
        }

        /// <summary>htmlParseEntityRef: the code point of a known entity followed by <c>;</c>, and the name read.</summary>
        int? ParseEntityRef(out string? name)
        {
            Advance();
            name = ParseName();
            if (name is not null && Cur == ';' && Html4Entities.CodePoints.TryGetValue(name, out var codePoint))
            {
                Advance();
                return codePoint;
            }
            return null;
        }

        /// <summary>htmlParseHTMLName: a tag or attribute name, lowercased and cut at 100 characters.</summary>
        string? ParseHtmlName()
        {
            if (!char.IsAsciiLetter(Cur) && Cur is not ('_' or ':' or '.'))
            {
                return null;
            }
            var name = new StringBuilder();
            while (name.Length < maxNameLength && (char.IsAsciiLetterOrDigit(Cur) || Cur is ':' or '-' or '_' or '.'))
            {
                name.Append(char.ToLowerInvariant(Cur));
                position++;
            }
            return name.ToString();
        }

        /// <summary>htmlParseHTMLName_nonInvasive: the name of the tag at the cursor, without reading past it.</summary>
        string NameAhead()
        {
            var name = new StringBuilder();
            while (name.Length < maxNameLength && Next(1 + name.Length) is var c && (char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_'))
            {
                name.Append(char.ToLowerInvariant(c));
            }
            return name.ToString();
        }

        /// <summary>
        /// htmlParseName: an entity, processing instruction or doctype name, case kept. A name that
        /// runs to the end of the input is none.
        /// </summary>
        string? ParseName()
        {
            var end = position;
            if (end < text.Length && (char.IsAsciiLetter(text[end]) || text[end] is '_' or ':'))
            {
                end++;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '-' or ':' or '.'))
                {
                    end++;
                }
                if (end == text.Length)
                {
                    return null;
                }
                if (text[end] is > '\0' and < '\u0080')
                {
                    var name = text[position..end];
                    position = end;
                    return name;
                }
            }
            return ParseNameComplex();
        }

        // htmlParseNameComplex, for names with non-ASCII letters
        string? ParseNameComplex()
        {
            var start = position;
            var c = CurrentChar(out var length);
            if (c is ' ' or '>' or '/' || (!IsLetter(c) && c is not ('_' or ':')))
            {
                return null;
            }
            while (c is not (' ' or '>' or '/') && (IsLetter(c) || IsNameMark(c) || c is '.' or '-' or '_' or ':'))
            {
                position += length;
                c = CurrentChar(out length);
            }
            return text[start..position];
        }

        // IS_LETTER, and IS_DIGIT, IS_COMBINING or IS_EXTENDER, from Unicode categories
        static bool IsLetter(int c) => c > 0 && c <= 0xFFFF && char.IsLetter((char)c);

        static bool IsNameMark(int c) =>
            c > 0 && c <= 0xFFFF && CharUnicodeInfo.GetUnicodeCategory((char)c) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ModifierLetter;

        /// <summary>htmlParseAttribute: the name, and the value when there's an <c>=</c>.</summary>
        string? ParseAttribute(out string? value)
        {
            value = null;
            var name = ParseHtmlName();
            if (name is null)
            {
                return null;
            }
            SkipBlanks();
            if (Cur == '=')
            {
                Advance();
                SkipBlanks();
                value = ParseAttributeValue();
            }
            return name;
        }

        // htmlParseAttValue
        string ParseAttributeValue()
        {
            if (Cur is not ('"' or '\''))
            {
                return ParseAttributeText('\0');
            }
            var quote = Cur;
            Advance();
            var value = ParseAttributeText(quote);
            if (Cur == quote)
            {
                Advance();
            }
            return value;
        }

        /// <summary>
        /// htmlParseHTMLAttribute: up to the quote, or (unquoted) a blank or <c>&gt;</c>, with
        /// references decoded. An invalid character reference decodes to NUL, which ends the
        /// value as libxml2 reads it.
        /// </summary>
        string ParseAttributeText(char stop)
        {
            var output = new StringBuilder();
            while (Cur != '\0' && Cur != stop)
            {
                if (stop == '\0' && (Cur == '>' || IsBlank(Cur)))
                {
                    break;
                }
                if (Cur == '&' && Next(1) == '#')
                {
                    var c = ParseCharRef();
                    if (c == 0)
                    {
                        output.Append('\0');
                    }
                    else
                    {
                        AppendCodePoint(output, c);
                    }
                }
                else if (Cur == '&')
                {
                    var entity = ParseEntityRef(out var name);
                    output.Append(name is null ? "&" : entity is { } codePoint ? char.ConvertFromUtf32(codePoint) : "&" + name);
                }
                else
                {
                    var c = CurrentChar(out var length);
                    AppendCodePoint(output, c);
                    position += length;
                }
            }
            var value = output.ToString();
            var nul = value.IndexOf('\0', StringComparison.Ordinal);
            return nul < 0 ? value : value[..nul];
        }

        /// <summary>htmlParseComment: up to <c>--&gt;</c> or <c>--!&gt;</c>, or at once for <c>&lt;!--&gt;</c> and <c>&lt;!---&gt;</c>.</summary>
        void ParseComment()
        {
            position += 4;
            var q = CurrentChar(out var ql);
            if (q == 0)
            {
                return;
            }
            if (q == '>')
            {
                Advance();
                return;
            }
            position += ql;
            var r = CurrentChar(out var rl);
            if (r == 0)
            {
                return;
            }
            if (q == '-' && r == '>')
            {
                Advance();
                return;
            }
            position += rl;
            var cur = CurrentChar(out var length);
            while (cur != 0 && (cur != '>' || r != '-' || q != '-'))
            {
                position += length;
                var next = CurrentChar(out var nextLength);
                if (q == '-' && r == '-' && cur == '!' && next == '>')
                {
                    cur = '>';
                    break;
                }
                q = r;
                r = cur;
                cur = next;
                length = nextLength;
            }
            if (cur == '>')
            {
                Advance();
            }
        }

        // htmlSkipBogusComment: <! that isn't a comment or a doctype, up to the next >
        void SkipBogusComment()
        {
            while (Cur != '\0')
            {
                var c = Cur;
                Advance();
                if (c == '>')
                {
                    break;
                }
            }
        }

        // htmlParsePI: without a name, only the <? is read
        void ParseProcessingInstruction()
        {
            position += 2;
            if (ParseName() is null)
            {
                return;
            }
            if (Cur == '>')
            {
                position++;
                return;
            }
            SkipBlanks();
            var c = CurrentChar(out var length);
            while (c is not (0 or '>'))
            {
                position += length;
                c = CurrentChar(out length);
            }
            if (c == '>')
            {
                position++;
            }
        }

        // htmlParseDocTypeDecl, for a doctype in the middle of the document
        void ParseDoctype()
        {
            position += 9;
            SkipBlanks();
            ParseName();
            SkipBlanks();
            if (UpcomingIs("SYSTEM"))
            {
                position += 6;
                SkipBlanks();
                ParseLiteral();
            }
            else if (UpcomingIs("PUBLIC"))
            {
                position += 6;
                SkipBlanks();
                ParseLiteral();
                SkipBlanks();
                if (Cur is '"' or '\'')
                {
                    ParseLiteral();
                }
            }
            SkipBlanks();
            while (Cur != '\0' && Cur != '>')
            {
                Advance();
            }
            if (Cur == '>')
            {
                Advance();
            }
        }

        bool UpcomingIs(string word)
        {
            for (var i = 0; i < word.Length; i++)
            {
                if (AsciiUpper(Next(i)) != word[i])
                {
                    return false;
                }
            }
            return true;
        }

        // htmlParseSystemLiteral and htmlParsePubidLiteral, read past
        void ParseLiteral()
        {
            if (Cur is not ('"' or '\''))
            {
                return;
            }
            var quote = Cur;
            Advance();
            while (Cur != '\0' && Cur != quote)
            {
                Advance();
            }
            if (Cur == quote)
            {
                Advance();
            }
        }
    }
}
