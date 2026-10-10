using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.RichText.Sanitize;
using static Campfire.RailsCompat.Ruby.RubyEscape;

namespace Campfire.RichText.Attachments;

/// <summary>
/// rails_autolink 1.1.8's <c>auto_link(text, html: { target: "_blank" }, sanitize_options: ...)</c>
/// as <c>MessagesHelper#message_presentation</c> calls it, Ruby regexp semantics included: <c>^</c>
/// and <c>$</c> anchor lines, <c>\w</c> and <c>\s</c> are ASCII, and the URL pattern runs over the
/// markup itself, so a URL inside an attribute value is linked where Rails links it.
/// </summary>
public static partial class RailsAutoLink
{
    const string linkAttributes = " target=\"_blank\"";

    // rails_autolink AUTO_LINK_RE's scheme list, matched with Onigmo /i (Unicode case fold).
    // The longest scheme is 6 letters; a fold never needs more source characters than that.
    const int maxSchemeChars = 6;

    static readonly HashSet<string> Schemes = new(StringComparer.Ordinal)
    {
        "ed2k", "ftp", "http", "https", "irc", "mailto", "news", "gopher", "nntp", "telnet", "webcal",
        "xmpp", "callto", "feed", "svn", "urn", "aim", "rsync", "tag", "ssh", "sftp", "rtsp", "afs", "file",
    };

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.!#$%&'*/=?^`{|}~+-])[A-Za-z0-9_.!#$%+-]\.?[A-Za-z0-9_.!#$%&'*/=?^`{|}~+-]*@[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+")]
    private static partial Regex AutoEmailPattern();

    // AUTO_LINK_CRE
    [GeneratedRegex(@"<[^>]+$", RegexOptions.Multiline)]
    private static partial Regex InsideTagBefore();

    [GeneratedRegex(@"^[^>]*>", RegexOptions.Multiline)]
    private static partial Regex InsideTagAfter();

    [GeneratedRegex(@"\G<a(?![A-Za-z0-9_]).*?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenAnchor();

    [GeneratedRegex(@"</a>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CloseAnchor();

    static readonly Dictionary<string, string> Brackets = new() { ["]"] = "[", [")"] = "(", ["}"] = "{" };

    /// <summary>
    /// <c>auto_link</c>: the text sanitized with <paramref name="sanitizeOptions"/>, then URLs and
    /// email addresses linked.
    /// </summary>
    public static string Apply(string text, SafeList sanitizeOptions)
    {
        if (RubyText.IsBlank(text))
        {
            return "";
        }
        var sanitized = SafeListSanitizer.Sanitize(text, sanitizeOptions);
        return AutoLinkEmailAddresses(AutoLinkUrls(sanitized));
    }

    static string AutoLinkUrls(string text)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < text.Length && TryFindUrl(text, index, out var start, out var length, out var hasScheme))
        {
            builder.Append(text, index, start - index);
            builder.Append(LinkUrl(text, start, length, hasScheme));
            index = start + length;
        }
        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    /// <summary>
    /// The next <c>AUTO_LINK_RE</c> match at or after <paramref name="from"/>. The scheme
    /// alternative is matched on a case-folded copy so <c>ſftp</c> links as <c>sftp</c> while the
    /// href keeps the original characters. <c>www\.\w</c> stays ASCII: <c>/i</c> does not make
    /// <c>\w</c> match <c>ſ</c>.
    /// </summary>
    static bool TryFindUrl(string text, int from, out int start, out int length, out bool hasScheme)
    {
        start = 0;
        length = 0;
        hasScheme = false;
        var best = int.MaxValue;
        var bestLength = 0;
        var bestScheme = false;
        var found = false;
        for (var i = from; i < text.Length; i++)
        {
            if (found && i > best + maxSchemeChars)
            {
                break;
            }
            if (i + 2 < text.Length && text[i] == ':' && text[i + 1] == '/' && text[i + 2] == '/'
                && TrySchemeBefore(text, i, from, out var schemeStart)
                && (schemeStart < best || (schemeStart == best && !bestScheme)))
            {
                var end = ConsumeUrlBody(text, i + 3);
                if (end > i + 3)
                {
                    best = schemeStart;
                    bestLength = end - schemeStart;
                    bestScheme = true;
                    found = true;
                }
            }
            if (IsWww(text, i) && i < best)
            {
                var end = ConsumeUrlBody(text, i + 5);
                if (end > i + 5)
                {
                    best = i;
                    bestLength = end - i;
                    bestScheme = false;
                    found = true;
                }
            }
        }
        if (!found)
        {
            return false;
        }
        start = best;
        length = bestLength;
        hasScheme = bestScheme;
        return true;
    }

    static bool TrySchemeBefore(string text, int colon, int minStart, out int schemeStart)
    {
        schemeStart = 0;
        var window = Math.Max(minStart, colon - maxSchemeChars);
        for (var at = window; at < colon; at++)
        {
            if (Schemes.Contains(Fold(text.AsSpan(at, colon - at))))
            {
                schemeStart = at;
                return true;
            }
        }
        return false;
    }

    // The folds Onigmo's /i applies that RegexOptions.IgnoreCase does not, plus the ASCII lower
    // the scheme list is written in. Full folds (ﬁ → fi, ß → ss) change length; the match is
    // still sliced from the original string.
    static string Fold(ReadOnlySpan<char> text)
    {
        var folded = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && char.IsSurrogatePair(text[i], text[i + 1]))
            {
                folded.Append(text[i]);
                folded.Append(text[i + 1]);
                i++;
                continue;
            }
            folded.Append(text[i] switch
            {
                '\u017F' => "s",
                '\u00DF' or '\u1E9E' => "ss",
                '\uFB00' => "ff",
                '\uFB01' => "fi",
                '\uFB02' => "fl",
                '\uFB03' => "ffi",
                '\uFB04' => "ffl",
                '\uFB05' or '\uFB06' => "st",
                '\u212A' => "k",
                _ => char.ToLowerInvariant(text[i]).ToString(),
            });
        }
        return folded.ToString();
    }

    static bool IsWww(string text, int i) =>
        i + 4 < text.Length
        && text[i] is 'w' or 'W' && text[i + 1] is 'w' or 'W' && text[i + 2] is 'w' or 'W'
        && text[i + 3] == '.'
        && text[i + 4] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';

    static int ConsumeUrlBody(string text, int start)
    {
        var i = start;
        while (i < text.Length && text[i] is not (' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '<' or '\u00A0' or '"'))
        {
            i++;
        }
        return i;
    }

    static string LinkUrl(string text, int start, int length, bool hasScheme)
    {
        var matched = text.Substring(start, length);
        if (AutoLinked(text[..start], text[(start + length)..]))
        {
            return matched;
        }

        var href = matched;
        var punctuation = new List<string>();
        // don't include trailing punctuation characters as part of the URL
        while (href.Length > 0 && LastCharacter(href) is var last && !IsUrlEndCharacter(last))
        {
            href = href[..^last.Length];
            punctuation.Add(last);
            if (Brackets.TryGetValue(last, out var opening) && Count(href, opening) > Count(href, last))
            {
                href += last;
                punctuation.RemoveAt(punctuation.Count - 1);
                break;
            }
        }

        // don't include a trailing &gt; entity as part of the URL
        var trailingGt = "";
        if (href.EndsWith("&gt;", StringComparison.Ordinal))
        {
            href = href[..^4];
            trailingGt = "&gt;";
        }

        var linkText = SafeListSanitizer.Sanitize(href, SafeList.Defaults);
        if (!hasScheme)
        {
            href = "http://" + href;
        }
        href = SafeListSanitizer.Sanitize(href, SafeList.Defaults);

        // content_tag(:a, ..., escape = false) + punctuation (a String, so SafeBuffer#+ escapes it)
        punctuation.Reverse();
        return $"<a{linkAttributes} href=\"{href.Replace("\"", "&quot;")}\">{linkText}</a>" +
            HtmlEscape(string.Concat(punctuation)) + trailingGt;
    }

    static string AutoLinkEmailAddresses(string text) =>
        AutoEmailPattern().Replace(text, match =>
        {
            if (AutoLinked(text[..match.Index], text[(match.Index + match.Length)..]))
            {
                return match.Value;
            }

            // display_text is sanitized (and so html_safe, left unescaped) only when sanitizing changed it
            var email = SafeListSanitizer.Sanitize(match.Value, SafeList.Defaults);
            var display = email == match.Value ? HtmlEscape(match.Value) : email;
            var href = "mailto:" + UrlEncode(email).Replace("%40", "@", StringComparison.Ordinal);
            return $"<a{linkAttributes} href=\"{HtmlEscape(href)}\">{display}</a>";
        });

    /// <summary><c>auto_linked?(left, right)</c>: inside a tag, or after an <c>&lt;a&gt;</c> that isn't closed yet.</summary>
    static bool AutoLinked(string left, string right)
    {
        if (InsideTagBefore().IsMatch(left) && InsideTagAfter().IsMatch(right))
        {
            return true;
        }
        // left.rindex(/<a\b.*?>/i) and $' !~ /<\/a>/i
        for (var start = left.LastIndexOf('<'); start >= 0; start = start == 0 ? -1 : left.LastIndexOf('<', start - 1))
        {
            var anchor = OpenAnchor().Match(left, start);
            if (anchor.Success)
            {
                return !CloseAnchor().IsMatch(left[(anchor.Index + anchor.Length)..]);
            }
        }
        return false;
    }

    static string LastCharacter(string s) =>
        s.Length >= 2 && char.IsSurrogatePair(s[^2], s[^1]) ? s[^2..] : s[^1..];

    /// <summary>What <c>/[^\p{Word}\/\-=;]$/</c> leaves at the end of a URL.</summary>
    static bool IsUrlEndCharacter(string character)
    {
        if (character is "/" or "-" or "=" or ";" or "‌" or "‍")
        {
            return true;
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(character, 0);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.ConnectorPunctuation;
    }

    static int Count(string s, string value)
    {
        var count = 0;
        for (var index = s.IndexOf(value, StringComparison.Ordinal); index >= 0; index = s.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
