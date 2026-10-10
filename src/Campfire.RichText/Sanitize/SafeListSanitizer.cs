using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.RichText.Html;

namespace Campfire.RichText.Sanitize;

/// <summary>
/// Port of Rails::HTML5::SafeListSanitizer and Loofah's HTML5 permit scrubber
/// (rails-html-sanitizer 1.7.1, loofah 2.25.2).
/// </summary>
public static partial class SafeListSanitizer
{
    static readonly HashSet<string> AttrValIsUri = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "cite", "href", "longdesc", "poster", "preload", "src", "xlink:href", "xml:base"
    };

    static readonly HashSet<string> AllowedProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "afs", "aim", "callto", "data", "ed2k", "fax", "ftp", "gopher", "http", "https", "irc", "line", "mailto", "modem", "news", "nntp",
        "rsync", "rtsp", "sftp", "sms", "ssh", "tag", "tel", "telnet", "urn", "webcal", "xmpp"
    };

    static readonly HashSet<string> AllowedDataMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif", "image/jpeg", "image/png", "text/css", "text/plain"
    };

    static readonly HashSet<string> AllowedStyleProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "background-color"
    };

    [GeneratedRegex(@"\A(?:[a-z]+|#[0-9a-f]{3,8}|var\(\s*--[a-z0-9_-]+\s*\)|(?:rgb|rgba|hsl|hsla)\([0-9a-z.,%\s/+-]*\))\z", RegexOptions.IgnoreCase)]
    private static partial Regex PlainColorRegex();

    /// <summary>
    /// Sanitizes HTML markup using the provided allowlist.
    /// </summary>
    public static string Sanitize(string html, SafeList list)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var fragment = HtmlParser.ParseFragment(html);
        ScrubFragment(fragment, list);
        return fragment.ToHtml();
    }

    /// <summary>
    /// Sanitizes HTML markup and serializes with &lt; and &gt; escaped in attribute values
    /// so the output can be scanned with regular expressions without mistaking attribute values for tags.
    /// </summary>
    public static string SanitizeWithEscapedAttributeBrackets(string html, SafeList list)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var fragment = HtmlParser.ParseFragment(html);
        ScrubFragment(fragment, list);
        return HtmlSerializerWithBracketEscaping.Serialize(fragment);
    }

    public static void ScrubFragment(HtmlFragment fragment, SafeList list)
    {
        var children = fragment.Children.ToArray();
        foreach (var child in children)
        {
            ScrubBottomUp(child, list);
        }
    }

    static void ScrubBottomUp(HtmlNode node, SafeList list)
    {
        if (node is HtmlParentNode parent)
        {
            var children = parent.Children.ToArray();
            foreach (var child in children)
            {
                ScrubBottomUp(child, list);
            }
        }

        Scrub(node, list);
    }

    static void Scrub(HtmlNode node, SafeList list)
    {
        if (node is HtmlText)
        {
            return;
        }

        if (node is not HtmlElement element)
        {
            // Non-elements (comments, doctypes, etc.) are stripped unless kept
            node.Remove();
            return;
        }

        var keep = list.AllowsTag(element.Tag);
        if (!keep)
        {
            // Unwrap HTML elements; drop foreign (SVG, MathML) elements with their contents
            if (element.Namespace == HtmlNamespace.Html)
            {
                var children = element.Children.ToArray();
                element.Parent?.ReplaceChild(element, children);
            }
            else
            {
                element.Remove();
            }
            return;
        }

        ScrubAttributes(element, list);
    }

    static void ScrubAttributes(HtmlElement element, SafeList list)
    {
        var isA = element.IsHtml("a");
        var i = 0;
        while (i < element.AttributeList.Count)
        {
            var attr = element.AttributeList[i];
            var name = attr.QualifiedName;

            var isUri = AttrValIsUri.Contains(name);
            var scrubbed = !list.AllowsAttribute(name) || (isUri && !AllowedUri(attr.Value));
            var isBlankSrc = name.Equals("src", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(attr.Value);

            if (scrubbed)
            {
                element.AttributeList.RemoveAt(i);
                continue;
            }

            if (isBlankSrc)
            {
                element.AttributeList.RemoveAt(i);
            }
            else
            {
                i++;
            }

            ForceCorrectAttributeEscaping(element.AttributeList, isA);
        }

        ScrubStyle(element);
    }

    static void ForceCorrectAttributeEscaping(List<HtmlAttr> attrs, bool isA)
    {
        foreach (var attr in attrs)
        {
            var name = attr.QualifiedName;
            var qualifies = name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("action", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                            (isA && name.Equals("name", StringComparison.OrdinalIgnoreCase));

            if (qualifies && attr.Value.Any(IsEscapedChar))
            {
                attr.Value = EscapeAttributeValue(attr.Value);
            }
        }
    }

    static bool IsEscapedChar(char c) =>
        c == ' ' || c == '"' || (c < ' ' && c != '\t' && c != '\n' && c != '\r');

    static string EscapeAttributeValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case ' ':
                    sb.Append("%20");
                    break;
                case '"':
                    sb.Append("%22");
                    break;
                case '\t' or '\n' or '\r':
                    sb.Append(c);
                    break;
                case < ' ':
                    // Drop C0 controls
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    static void ScrubStyle(HtmlElement element)
    {
        var style = element.GetAttribute("style");
        if (style is null)
        {
            return;
        }

        var parts = style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var declarations = new List<(string Property, string Value)>();
        foreach (var part in parts)
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex >= 0)
            {
                var prop = part[..colonIndex].Trim().ToLowerInvariant();
                var val = part[(colonIndex + 1)..].Trim();
                declarations.Add((prop, val));
            }
        }

        static bool IsAllowed((string Property, string Value) d)
        {
            if (d.Property == "white-space")
            {
                return d.Value is "normal" or "nowrap" or "pre" or "pre-wrap" or "pre-line";
            }
            return AllowedStyleProperties.Contains(d.Property) && PlainColorRegex().IsMatch(d.Value);
        }

        if (declarations.Count > 0 && declarations.All(IsAllowed))
        {
            // Format without spaces matching Loofah/Crass
            var formatted = declarations.Select(d => $"{d.Property}:{d.Value};").ToArray();
            NokogiriAttribute.Set(element, "style", string.Concat(formatted));
            return;
        }

        var kept = declarations.Where(IsAllowed).Select(d => $"{d.Property}:{d.Value};").ToArray();
        NokogiriAttribute.Set(element, "style", string.Concat(kept));
    }

    public static bool AllowedUri(string uri)
    {
        // 1. Filter out control characters: `\u0000-\u0020\u007f\u0080-\u0101`
        var sb = new StringBuilder(uri.Length);
        foreach (var c in uri)
        {
            if (!IsControlCharacter(c))
            {
                sb.Append(c);
            }
        }

        // 2. Decode numeric character references and HTML entities
        var decoded = DecodeNumericCharacterReferences(CgiUnescapeHtml(sb.ToString()));

        // 3. Filter control characters again
        sb.Clear();
        foreach (var c in decoded)
        {
            if (!IsControlCharacter(c))
            {
                sb.Append(c);
            }
        }

        var s = sb.ToString()
            .Replace("&Tab;", "", StringComparison.Ordinal)
            .Replace("&NewLine;", "", StringComparison.Ordinal)
            .Replace("&colon;", ":", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        var protocol = ExtractProtocol(s);
        if (protocol is null)
        {
            return true;
        }

        if (!AllowedProtocols.Contains(protocol))
        {
            return false;
        }

        if (protocol == "data")
        {
            var mediaType = ExtractDataUriMediaType(s);
            return mediaType is not null && AllowedDataMediaTypes.Contains(mediaType);
        }

        return true;
    }

    static bool IsControlCharacter(char c) =>
        c == '`' || c <= '\u0020' || c == '\u007f' || (c >= '\u0080' && c <= '\u0101');

    static string? ExtractProtocol(string s)
    {
        if (s.Length == 0 || s[0] < 'a' || s[0] > 'z')
        {
            return null;
        }

        var end = 1;
        while (end < s.Length && (char.IsAsciiLetterOrDigit(s[end]) || s[end] == '+' || s[end] == '-' || s[end] == '.'))
        {
            end++;
        }

        var rest = s[end..];
        if (HasProtocolSeparator(rest))
        {
            return s[..end];
        }

        return null;
    }

    static bool HasProtocolSeparator(string rest)
    {
        if (rest.StartsWith(':'))
        {
            return true;
        }

        if (rest.StartsWith("&#x", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = rest[3..].TrimStart('0');
            if (trimmed.StartsWith("3a", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (rest.StartsWith("&#", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = rest[2..].TrimStart('0');
            if (trimmed.StartsWith("58", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (rest.StartsWith("%3a", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("&#37;3a", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    static string? ExtractDataUriMediaType(string s)
    {
        var rest = s.StartsWith("data:", StringComparison.Ordinal) ? s[5..] : s;
        var commaIndex = rest.IndexOf(',');
        if (commaIndex < 0)
        {
            return null;
        }

        var metadata = rest[..commaIndex];
        if (metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            metadata = metadata[..^7];
        }

        var semiIndex = metadata.IndexOf(';');
        var mediaType = (semiIndex >= 0 ? metadata[..semiIndex] : metadata).Trim(' ', '\t', '\n', '\v', '\f', '\r', '\0');

        static bool IsTChar(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

        var slashIndex = mediaType.IndexOf('/');
        if (slashIndex > 0 && slashIndex < mediaType.Length - 1)
        {
            var type = mediaType[..slashIndex];
            var sub = mediaType[(slashIndex + 1)..];
            if (type.All(IsTChar) && sub.All(IsTChar))
            {
                return mediaType;
            }
        }

        return "text/plain";
    }

    static string CgiUnescapeHtml(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '&')
            {
                if (TryUnescapeEntity(s, i, out var replacement, out var consumed))
                {
                    sb.Append(replacement);
                    i += consumed;
                    continue;
                }
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    static bool TryUnescapeEntity(string s, int start, out string replacement, out int consumed)
    {
        ReadOnlySpan<(string Name, string Value)> named =
        [
            ("&apos;", "'"),
            ("&amp;", "&"),
            ("&quot;", "\""),
            ("&gt;", ">"),
            ("&lt;", "<")
        ];

        var span = s.AsSpan(start);
        foreach (var (name, value) in named)
        {
            if (span.StartsWith(name, StringComparison.Ordinal))
            {
                replacement = value;
                consumed = name.Length;
                return true;
            }
        }

        var semi = s.IndexOf(';', start);
        if (semi > start)
        {
            var body = s.AsSpan(start + 1, semi - start - 1);
            if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseNumericReference(body[2..], 16, out var ch))
                {
                    replacement = ch.ToString();
                    consumed = semi - start + 1;
                    return true;
                }
            }
            else if (body.StartsWith("#", StringComparison.Ordinal))
            {
                if (TryParseNumericReference(body[1..], 10, out var ch))
                {
                    replacement = ch.ToString();
                    consumed = semi - start + 1;
                    return true;
                }
            }
        }

        replacement = string.Empty;
        consumed = 0;
        return false;
    }

    static bool TryParseNumericReference(ReadOnlySpan<char> digits, int radix, out string result)
    {
        result = string.Empty;
        if (digits.IsEmpty)
        {
            return false;
        }

        var trimmed = digits.TrimStart('0');
        var maxLen = radix == 16 ? 6 : 7;
        if (trimmed.Length > maxLen)
        {
            return false;
        }

        var str = trimmed.IsEmpty ? "0" : trimmed.ToString();
        if (uint.TryParse(str, radix == 16 ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) &&
            code < 0x10ffff && !char.IsSurrogate((char)(code <= 0xffff ? code : 0)))
        {
            try
            {
                result = char.ConvertFromUtf32((int)code);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    static string DecodeNumericCharacterReferences(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '&' && i + 1 < s.Length && s[i + 1] == '#')
            {
                var start = i + 2;
                var hex = start < s.Length && (s[start] == 'x' || s[start] == 'X');
                var digitsStart = hex ? start + 1 : start;
                var end = digitsStart;

                while (end < s.Length && (hex ? char.IsAsciiHexDigit(s[end]) : char.IsAsciiDigit(s[end])))
                {
                    end++;
                }

                if (end > digitsStart)
                {
                    var fullEnd = end < s.Length && s[end] == ';' ? end + 1 : end;
                    var digits = s[digitsStart..end];
                    var radix = hex ? 16 : 10;
                    if (TryParseNumericReference(digits, radix, out var decoded))
                    {
                        sb.Append(decoded);
                    }
                    else
                    {
                        sb.Append(s[i..fullEnd]);
                    }
                    i = fullEnd;
                    continue;
                }
            }

            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Serializes with &lt; and &gt; escaped in attribute values so regexes can't confuse them with tags.
/// </summary>
internal static class HtmlSerializerWithBracketEscaping
{
    static readonly HashSet<string> VoidElements =
    [
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr",
        "img", "input", "keygen", "link", "meta", "param", "source", "track", "wbr",
    ];

    public static string Serialize(HtmlNode node)
    {
        var sb = new StringBuilder();
        SerializeNode(node, sb);
        return sb.ToString();
    }

    static void SerializeNode(HtmlNode node, StringBuilder sb)
    {
        switch (node)
        {
            case HtmlFragment fragment:
                foreach (var child in fragment.Children)
                {
                    SerializeNode(child, sb);
                }
                break;
            case HtmlElement element:
                sb.Append('<').Append(element.Name);
                foreach (var attr in element.Attributes)
                {
                    sb.Append(' ').Append(attr.QualifiedName).Append("=\"");
                    SerializeAttributeValue(attr.Value, sb);
                    sb.Append('"');
                }
                sb.Append('>');
                if (element.Namespace == HtmlNamespace.Html && VoidElements.Contains(element.Tag))
                {
                    return;
                }
                foreach (var child in element.Children)
                {
                    SerializeNode(child, sb);
                }
                sb.Append("</").Append(element.Name).Append('>');
                break;
            default:
                HtmlSerializer.Write(sb, node);
                break;
        }
    }

    static void SerializeAttributeValue(string value, StringBuilder sb)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '\u00a0':
                    sb.Append("&#160;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }
}
