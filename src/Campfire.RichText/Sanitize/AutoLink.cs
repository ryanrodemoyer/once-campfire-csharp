using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.RichText.Sanitize;

/// <summary>
/// Port of rails_autolink 1.1.8's auto_link helper, as called by MessagesHelper#message_presentation.
/// Serializes with &lt; and &gt; escaped in attribute values so links are only inserted between tags.
/// </summary>
public static partial class AutoLink
{
    [GeneratedRegex(@"(?:(?i:((?:ed2k|ftp|http|https|irc|mailto|news|gopher|nntp|telnet|webcal|xmpp|callto|feed|svn|urn|aim|rsync|tag|ssh|sftp|rtsp|afs|file):))//|(?i:www)\.[a-zA-Z0-9_])[^ \t\r\n\x0B\x0C<\u00A0""]+", RegexOptions.Compiled)]
    private static partial Regex AutoLinkPattern();

    [GeneratedRegex(@"\A[a-zA-Z0-9_.!#$%+-]\.?[a-zA-Z0-9_.!#$%&'*/=?^`{|}~+-]*@[a-zA-Z0-9_-]+(?:\.[a-zA-Z0-9_-]+)+", RegexOptions.Compiled)]
    private static partial Regex AutoEmailPattern();

    [GeneratedRegex(@"(?i)\A<a\b.*?>", RegexOptions.Compiled)]
    private static partial Regex OpenAnchorPattern();

    public static string Apply(string html, SafeList? sanitizeOptions = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = SafeListSanitizer.SanitizeWithEscapedAttributeBrackets(html, sanitizeOptions ?? SafeList.AutoLink);
        text = AutoLinkUrls(text);
        return AutoLinkEmailAddresses(text);
    }

    static string AutoLinkUrls(string text)
    {
        var sb = new StringBuilder(text.Length);
        var last = 0;
        var tags = new TagIndex(text);

        foreach (Match match in AutoLinkPattern().Matches(text))
        {
            sb.Append(text[last..match.Index]);
            last = match.Index + match.Length;

            var schemeGroup = match.Groups[1];
            var href = match.Value;

            if (tags.AutoLinked(match.Index, match.Index + match.Length))
            {
                sb.Append(href);
                continue;
            }

            var punctuation = new List<char>();
            var brackets = new BracketCounts(href);

            while (href.Length > 0)
            {
                var c = href[^1];
                if (IsWordChar(c) || c is '/' or '-' or '=' or ';')
                {
                    break;
                }

                href = href[..^1];
                punctuation.Add(c);
                brackets.Remove(c);

                var opening = OpeningBracket(c);
                if (opening is not null && brackets.Count(opening.Value) > brackets.Count(c))
                {
                    href += punctuation[^1];
                    punctuation.RemoveAt(punctuation.Count - 1);
                    break;
                }
            }

            var trailingGt = string.Empty;
            if (href.EndsWith("&gt;", StringComparison.Ordinal))
            {
                href = href[..^4];
                trailingGt = "&gt;";
            }

            var linkText = href;
            if (!schemeGroup.Success)
            {
                href = $"http://{href}";
            }

            var sanitizedLinkText = SafeListSanitizer.Sanitize(linkText, SafeList.Defaults);
            var sanitizedHref = SafeListSanitizer.Sanitize(href, SafeList.Defaults);

            sb.Append("<a target=\"_blank\" href=\"")
              .Append(sanitizedHref.Replace("\"", "&quot;"))
              .Append("\">")
              .Append(sanitizedLinkText)
              .Append("</a>");

            punctuation.Reverse();
            sb.Append(HtmlEscape(new string(punctuation.ToArray())));
            sb.Append(trailingGt);
        }

        sb.Append(text[last..]);
        return sb.ToString();
    }

    static string AutoLinkEmailAddresses(string text)
    {
        var sb = new StringBuilder(text.Length);
        var copied = 0;
        var position = 0;
        var tags = new TagIndex(text);

        while (position < text.Length)
        {
            var precededByLocal = position > 0 && IsEmailLocalChar(text[position - 1]);
            Match? m = null;
            if (!precededByLocal)
            {
                var match = AutoEmailPattern().Match(text, position);
                if (match.Success && match.Index == position)
                {
                    m = match;
                }
            }

            if (m is null)
            {
                position++;
                continue;
            }

            var start = m.Index;
            var end = m.Index + m.Length;
            var email = m.Value;

            sb.Append(text[copied..start]);

            if (tags.AutoLinked(start, end))
            {
                sb.Append(email);
            }
            else
            {
                var sanitized = SafeListSanitizer.Sanitize(email, SafeList.Defaults);
                var display = sanitized == email ? HtmlEscape(email) : sanitized;
                var href = $"mailto:{WebUtility.UrlEncode(sanitized).Replace("%40", "@")}";
                sb.Append("<a target=\"_blank\" href=\"")
                  .Append(HtmlEscape(href))
                  .Append("\">")
                  .Append(display)
                  .Append("</a>");
            }

            copied = end;
            position = Math.Max(end, position + 1);
        }

        sb.Append(text[copied..]);
        return sb.ToString();
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    static bool IsEmailLocalChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "_.!#$%&'*/=?^`{|}~+-".Contains(c);

    static char? OpeningBracket(char closing) => closing switch
    {
        ']' => '[',
        ')' => '(',
        '}' => '{',
        _ => null
    };

    static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");

    sealed class BracketCounts
    {
        static readonly char[] Brackets = ['[', ']', '(', ')', '{', '}'];
        readonly int[] counts = new int[6];

        public BracketCounts(string s)
        {
            foreach (var c in s)
            {
                var idx = Array.IndexOf(Brackets, c);
                if (idx >= 0) counts[idx]++;
            }
        }

        public int Count(char bracket)
        {
            var idx = Array.IndexOf(Brackets, bracket);
            return idx >= 0 ? counts[idx] : 0;
        }

        public void Remove(char c)
        {
            var idx = Array.IndexOf(Brackets, c);
            if (idx >= 0 && counts[idx] > 0) counts[idx]--;
        }
    }

    sealed class TagIndex
    {
        readonly List<int> lts = [];
        readonly List<int> gts = [];
        readonly int? firstDanglingNewline;
        readonly List<(int Start, int End)> openAnchors = [];
        readonly List<int> closeAnchors = [];

        public TagIndex(string text)
        {
            int? unclosedLt = null;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '<')
                {
                    lts.Add(i);
                    unclosedLt ??= i;

                    if (i + 3 < text.Length && text.Substring(i + 1, 3).Equals("/a>", StringComparison.OrdinalIgnoreCase))
                    {
                        closeAnchors.Add(i);
                    }

                    var afterPrevious = openAnchors.Count == 0 || openAnchors[^1].End <= i;
                    if (afterPrevious)
                    {
                        var m = OpenAnchorPattern().Match(text, i);
                        if (m.Success && m.Index == i)
                        {
                            openAnchors.Add((i, i + m.Length));
                        }
                    }
                }
                else if (c == '>')
                {
                    gts.Add(i);
                    unclosedLt = null;
                }
                else if (c == '\n' && firstDanglingNewline is null && unclosedLt is not null && unclosedLt.Value + 2 <= i)
                {
                    firstDanglingNewline = i;
                }
            }
        }

        public bool AutoLinked(int start, int end) =>
            (OpenTagAtLineEnd(start) && ClosesTag(end)) || InsideAnchor(start);

        bool OpenTagAtLineEnd(int start)
        {
            if (firstDanglingNewline.HasValue && firstDanglingNewline.Value < start)
            {
                return true;
            }

            var gtIdx = gts.BinarySearch(start);
            if (gtIdx < 0) gtIdx = ~gtIdx - 1;

            int? lastGt = gtIdx >= 0 && gtIdx < gts.Count ? gts[gtIdx] : null;

            var ltIdx = 0;
            if (lastGt.HasValue)
            {
                ltIdx = lts.BinarySearch(lastGt.Value);
                if (ltIdx < 0) ltIdx = ~ltIdx;
                else ltIdx++;
            }

            return ltIdx < lts.Count && lts[ltIdx] + 2 <= start;
        }

        bool ClosesTag(int end) => gts.Count > 0 && gts[^1] >= end;

        bool InsideAnchor(int start)
        {
            var idx = openAnchors.BinarySearch((start, int.MaxValue), Comparer<(int Start, int End)>.Create((a, b) => a.Start.CompareTo(b.Start)));
            if (idx < 0) idx = ~idx - 1;

            if (idx < 0 || idx >= openAnchors.Count) return false;

            var (_, anchorEnd) = openAnchors[idx];
            if (anchorEnd > start) return true;

            var closeIdx = closeAnchors.BinarySearch(anchorEnd);
            if (closeIdx < 0) closeIdx = ~closeIdx;

            return closeIdx >= closeAnchors.Count || closeAnchors[closeIdx] + 4 > start;
        }
    }
}
