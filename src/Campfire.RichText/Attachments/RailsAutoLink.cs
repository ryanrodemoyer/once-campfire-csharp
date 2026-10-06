using System.Globalization;
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

    [GeneratedRegex(
        @"(?:((?:ed2k|ftp|http|https|irc|mailto|news|gopher|nntp|telnet|webcal|xmpp|callto|feed|svn|urn|aim|rsync|tag|ssh|sftp|rtsp|afs|file):)//|www\.[A-Za-z0-9_])[^ \t\n\v\f\r< ""]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoLinkPattern();

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

    static string AutoLinkUrls(string text) =>
        AutoLinkPattern().Replace(text, match =>
        {
            if (AutoLinked(text[..match.Index], text[(match.Index + match.Length)..]))
            {
                return match.Value;
            }

            var href = match.Value;
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
            if (!match.Groups[1].Success)
            {
                href = "http://" + href;
            }
            href = SafeListSanitizer.Sanitize(href, SafeList.Defaults);

            // content_tag(:a, ..., escape = false) + punctuation (a String, so SafeBuffer#+ escapes it)
            punctuation.Reverse();
            return $"<a{linkAttributes} href=\"{href.Replace("\"", "&quot;")}\">{linkText}</a>" +
                HtmlEscape(string.Concat(punctuation)) + trailingGt;
        });

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
