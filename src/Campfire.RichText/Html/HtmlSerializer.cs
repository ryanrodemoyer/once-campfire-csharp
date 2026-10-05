using System.Buffers;
using System.Text;

namespace Campfire.RichText.Html;

/// <summary>
/// Nokogiri's HTML5 serializer, <c>html_standard_serialize</c> (nokogiri 1.19.4,
/// ext/nokogiri/xml_node.c), which <c>to_html</c> uses for every node of an HTML5 document and so
/// what <c>ActionText::HtmlConversion.node_to_html</c> and Loofah produce.
/// </summary>
public static class HtmlSerializer
{
    // Only HTML elements count: Nokogiri's is_one_of skips any node with a namespace.
    static readonly HashSet<string> VoidElements =
    [
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr",
        "img", "input", "keygen", "link", "meta", "param", "source", "track", "wbr",
    ];

    static readonly HashSet<string> UnescapedTextElements =
    [
        "style", "script", "xmp", "iframe", "noembed", "noframes", "plaintext", "noscript",
    ];

    static readonly SearchValues<char> TextSpecial = SearchValues.Create("&\u00a0<>");
    static readonly SearchValues<char> AttributeSpecial = SearchValues.Create("&\u00a0\"");

    /// <summary>The node itself, or a document's or fragment's children.</summary>
    public static string Serialize(HtmlNode node)
    {
        var output = new StringBuilder();
        Write(output, node);
        return output.ToString();
    }

    /// <summary>A node's children (Nokogiri's <c>inner_html</c>).</summary>
    public static string SerializeChildren(HtmlParentNode node)
    {
        var output = new StringBuilder();
        foreach (var child in node.Children)
        {
            Write(output, child);
        }
        return output.ToString();
    }

    public static void Write(StringBuilder output, HtmlNode node)
    {
        switch (node)
        {
            case HtmlElement element:
                WriteElement(output, element);
                break;
            case HtmlText text:
                if (text.Parent is HtmlElement { Namespace: HtmlNamespace.Html } parent && UnescapedTextElements.Contains(parent.Name))
                {
                    output.Append(text.Data);
                }
                else
                {
                    WriteEscaped(output, text.Data, TextSpecial);
                }
                break;
            case HtmlCData cdata:
                output.Append("<![CDATA[").Append(cdata.Data).Append("]]>");
                break;
            case HtmlComment comment:
                output.Append("<!--").Append(comment.Data).Append("-->");
                break;
            case HtmlDoctype doctype:
                output.Append("<!DOCTYPE ").Append(doctype.Name).Append('>');
                break;
            case HtmlParentNode container:
                foreach (var child in container.Children)
                {
                    Write(output, child);
                }
                break;
        }
    }

    static void WriteElement(StringBuilder output, HtmlElement element)
    {
        // HTML, SVG and MathML elements are written without a namespace prefix
        output.Append('<').Append(element.Name);
        foreach (var attribute in element.Attributes)
        {
            output.Append(' ').Append(attribute.QualifiedName).Append("=\"");
            WriteEscaped(output, attribute.Value, AttributeSpecial);
            output.Append('"');
        }
        output.Append('>');

        if (element.Namespace == HtmlNamespace.Html && VoidElements.Contains(element.Name))
        {
            return;
        }
        foreach (var child in element.Children)
        {
            Write(output, child);
        }
        output.Append("</").Append(element.Name).Append('>');
    }

    // output_escaped_string: & and U+00A0 always; " in attribute values; < and > in text.
    static void WriteEscaped(StringBuilder output, string value, SearchValues<char> special)
    {
        var rest = value.AsSpan();
        var next = rest.IndexOfAny(special);
        while (next >= 0)
        {
            output.Append(rest[..next]).Append(rest[next] switch
            {
                '&' => "&amp;",
                '\u00a0' => "&nbsp;",
                '"' => "&quot;",
                '<' => "&lt;",
                _ => "&gt;",
            });
            rest = rest[(next + 1)..];
            next = rest.IndexOfAny(special);
        }
        output.Append(rest);
    }
}
