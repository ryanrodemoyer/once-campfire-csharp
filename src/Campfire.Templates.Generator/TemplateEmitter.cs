using System.Globalization;
using System.Text;

namespace Campfire.Templates.Generator;

/// <summary>
/// Turns scanned segments into the statements of a render method. Text becomes
/// <c>writer.WriteLiteral("..."u8)</c>, <c>&lt;%= x %&gt;</c> becomes <c>writer.Append((x))</c>,
/// <c>&lt;%== x %&gt;</c> becomes <c>writer.AppendRaw((x))</c> and code is copied as is, so
/// output order is exactly Erubi's.
/// <para>
/// An expression that leaves brackets open, like <c>&lt;%= Helper(x, () =&gt; { %&gt;</c>, is a
/// block expression (Rails' <c>BLOCK_EXPR</c>, <c>&lt;%= helper x do %&gt;</c>). Its call stays open
/// until the code tag that brings the depth back, like <c>&lt;% }) %&gt;</c>, which is followed by
/// the closing <c>);</c>.
/// </para>
/// </summary>
sealed class TemplateEmitter(string writer, string templatePath, string indent)
{
    readonly StringBuilder output = new();
    readonly Stack<int> openBlocks = new();
    int depth;

    /// <summary>Line of the first block expression left open, or 0 when every block closed.</summary>
    public int UnclosedBlockLine { get; private set; }

    public string Emit(IEnumerable<Segment> segments)
    {
        var blockLines = new Stack<int>();
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Text:
                    // Erubi puts template text into Ruby string literals, and Ruby's parser (parse.y
                    // and Prism alike) reads a CRLF in a literal as LF. So Rails renders CRLF lines
                    // with LF; a lone CR survives.
                    Line($"{writer}.WriteLiteral({Utf8Literal(segment.Value.Replace("\r\n", "\n"))});");
                    break;
                case SegmentKind.Code:
                    if (segment.Value.Trim().Length == 0)
                    {
                        break; // a comment or empty tag
                    }
                    LineDirective(segment.Line);
                    output.Append(segment.Value).Append('\n');
                    depth += CodeDepth.Delta(segment.Value);
                    while (openBlocks.Count > 0 && depth == openBlocks.Peek() + 1)
                    {
                        openBlocks.Pop();
                        blockLines.Pop();
                        depth--;
                        Line(");");
                    }
                    break;
                default:
                    var method = segment.Kind == SegmentKind.Expression ? "Append" : "AppendRaw";
                    var delta = CodeDepth.Delta(segment.Value);
                    LineDirective(segment.Line);
                    if (delta > 0)
                    {
                        openBlocks.Push(depth);
                        blockLines.Push(segment.Line);
                        output.Append(indent).Append(writer).Append('.').Append(method).Append('(')
                            .Append(segment.Value).Append('\n');
                        depth += delta + 1;
                    }
                    else
                    {
                        output.Append(indent).Append(writer).Append('.').Append(method).Append("((")
                            .Append(segment.Value).Append("));\n");
                    }
                    break;
            }
        }
        while (blockLines.Count > 0)
        {
            UnclosedBlockLine = blockLines.Pop();
        }
        output.Append("#line default\n");
        return output.ToString();
    }

    void LineDirective(int line) =>
        output.Append("#line ").Append(line.ToString(CultureInfo.InvariantCulture)).Append(" \"")
            .Append(templatePath.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");

    void Line(string statement)
    {
        output.Append("#line hidden\n");
        output.Append(indent).Append(statement).Append('\n');
    }

    /// <summary>A C# UTF-8 string literal (<c>"..."u8</c>) holding exactly <paramref name="text"/>.</summary>
    public static string Utf8Literal(string text)
    {
        var literal = new StringBuilder(text.Length + 4).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    literal.Append("\\\"");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                case '\n':
                    literal.Append("\\n");
                    break;
                case '\r':
                    literal.Append("\\r");
                    break;
                case '\t':
                    literal.Append("\\t");
                    break;
                default:
                    if (c < ' ' || c == '\u007f' || c == '\u0085' || c == (char)0x2028 || c == (char)0x2029 || char.IsSurrogate(c))
                    {
                        literal.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        literal.Append(c);
                    }
                    break;
            }
        }
        return literal.Append("\"u8").ToString();
    }
}
