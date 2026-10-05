using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Templates.Generator;

enum SegmentKind
{
    /// <summary>Template text, written as is (<c>safe_append=</c>).</summary>
    Text,

    /// <summary>Code from <c>&lt;% %&gt;</c>, <c>&lt;%- %&gt;</c> or <c>&lt;%# %&gt;</c> (only newlines for a comment).</summary>
    Code,

    /// <summary><c>&lt;%= %&gt;</c>: appended, escaped unless HTML-safe (<c>append=</c>).</summary>
    Expression,

    /// <summary><c>&lt;%== %&gt;</c>: appended without escaping (<c>safe_expr_append=</c>).</summary>
    RawExpression,
}

/// <summary>One piece of a template. <see cref="Line"/> is the 1-based line its source starts on.</summary>
sealed class Segment(SegmentKind kind, string value, int line)
{
    public SegmentKind Kind { get; } = kind;
    public string Value { get; } = value;
    public int Line { get; } = line;
}

/// <summary>
/// Splits a template exactly as Rails compiles <c>.html.erb</c>: <c>Erubi::Engine</c> 1.13.1 with
/// <c>trim: true</c> and <c>escape: false</c>, as set up by
/// reference: actionview/lib/action_view/template/handlers/erb.rb and erb/erubi.rb.
/// The loop below is <c>Erubi::Engine#initialize</c> (lib/erubi.rb) line for line, so the text
/// segments come out byte-identical to the strings Erubi passes to <c>add_text</c>.
/// </summary>
static class ErbScanner
{
    // Erubi::Engine::DEFAULT_REGEXP. Ruby's /m is .NET's Singleline.
    static readonly Regex Tag = new(
        @"<%(={1,2}|-|#|%)?(.*?)([-=])?%>([ \t]*\r?\n)?",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // ActionView::Template::Handlers::ERB::ENCODING_TAG; Rails strips it before compiling.
    static readonly Regex EncodingTag = new(
        @"\A(<%#.*coding[:=]\s*(\S+)[ \t]*-?%>)[ \t]*",
        RegexOptions.CultureInvariant);

    static readonly Regex Blank = new(@"\A[ \t]*\z", RegexOptions.CultureInvariant);

    public static List<Segment> Scan(string input)
    {
        var encodingTag = EncodingTag.Match(input);
        if (encodingTag.Success)
        {
            input = input.Substring(encodingTag.Length);
        }

        var segments = new List<Segment>();
        var lines = new LineCounter(input);
        var pos = 0;
        var isBol = true;

        void AddText(string text, int at)
        {
            if (text.Length > 0)
            {
                segments.Add(new Segment(SegmentKind.Text, text, lines.LineAt(at)));
            }
        }

        void AddCode(string code, int at) => segments.Add(new Segment(SegmentKind.Code, code, lines.LineAt(at)));

        for (var match = Tag.Match(input); match.Success; match = match.NextMatch())
        {
            var indicator = match.Groups[1].Success ? match.Groups[1].Value : null;
            var code = match.Groups[2].Value;
            var tailch = match.Groups[3].Success ? match.Groups[3].Value : null;
            var rspace = match.Groups[4].Success ? match.Groups[4].Value : null;
            var textStart = pos;
            var text = input.Substring(pos, match.Index - pos);
            pos = match.Index + match.Length;
            var ch = indicator?[0];

            string? lspace = null;
            if (ch != '=')
            {
                if (text.Length == 0)
                {
                    if (isBol)
                    {
                        lspace = "";
                    }
                }
                else if (text[text.Length - 1] == '\n')
                {
                    lspace = "";
                }
                else
                {
                    var rindex = text.LastIndexOf('\n');
                    if (rindex >= 0)
                    {
                        var s = text.Substring(rindex + 1);
                        if (Blank.IsMatch(s))
                        {
                            lspace = s;
                            text = text.Substring(0, rindex + 1);
                        }
                    }
                    else if (isBol && Blank.IsMatch(text))
                    {
                        lspace = text;
                        text = "";
                    }
                }
            }

            isBol = rspace is not null;
            AddText(text, textStart);
            var codeStart = match.Groups[2].Index;
            var tagEnd = match.Index + match.Length;
            switch (ch)
            {
                case '=':
                    if (!string.IsNullOrEmpty(tailch))
                    {
                        rspace = null;
                    }
                    var kind = indicator == "=" ? SegmentKind.Expression : SegmentKind.RawExpression;
                    segments.Add(new Segment(kind, code, lines.LineAt(codeStart)));
                    if (rspace is not null)
                    {
                        AddText(rspace, tagEnd - rspace.Length);
                    }
                    break;
                case null:
                case '-':
                    if (lspace is not null && rspace is not null)
                    {
                        AddCode(lspace + code + rspace, codeStart);
                    }
                    else
                    {
                        if (lspace is not null)
                        {
                            AddText(lspace, codeStart);
                        }
                        AddCode(code, codeStart);
                        if (rspace is not null)
                        {
                            AddText(rspace, tagEnd - rspace.Length);
                        }
                    }
                    break;
                case '#':
                    var newlines = new string('\n', Count(code, '\n') + (rspace is not null ? 1 : 0));
                    if (lspace is not null && rspace is not null)
                    {
                        AddCode(newlines, codeStart);
                    }
                    else
                    {
                        if (lspace is not null)
                        {
                            AddText(lspace, codeStart);
                        }
                        AddCode(newlines, codeStart);
                        if (rspace is not null)
                        {
                            AddText(rspace, tagEnd - rspace.Length);
                        }
                    }
                    break;
                default: // '%': a literal "<%", Erubi's literal_prefix
                    AddText(lspace + "<%" + code + tailch + "%>" + rspace, match.Index);
                    break;
            }
        }

        AddText(input.Substring(pos), pos);
        return segments;
    }

    static int Count(string s, char c)
    {
        var n = 0;
        foreach (var x in s)
        {
            if (x == c)
            {
                n++;
            }
        }
        return n;
    }

    sealed class LineCounter(string input)
    {
        int offset;
        int line = 1;

        // Offsets only move forward, so counting is linear over the whole template.
        public int LineAt(int at)
        {
            if (at < offset)
            {
                offset = 0;
                line = 1;
            }
            for (; offset < at && offset < input.Length; offset++)
            {
                if (input[offset] == '\n')
                {
                    line++;
                }
            }
            return line;
        }
    }

    /// <summary>Concatenates adjacent text segments, as the emitter writes them.</summary>
    public static List<Segment> MergeText(List<Segment> segments)
    {
        var merged = new List<Segment>(segments.Count);
        var text = new StringBuilder();
        var textLine = 0;
        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Text)
            {
                if (text.Length == 0)
                {
                    textLine = segment.Line;
                }
                text.Append(segment.Value);
                continue;
            }
            if (text.Length > 0)
            {
                merged.Add(new Segment(SegmentKind.Text, text.ToString(), textLine));
                text.Clear();
            }
            merged.Add(segment);
        }
        if (text.Length > 0)
        {
            merged.Add(new Segment(SegmentKind.Text, text.ToString(), textLine));
        }
        return merged;
    }
}
