using System.Text;
using Campfire.RichText.Attachments;
using Campfire.RichText.Html;

namespace Campfire.RichText.PlainText;

/// <summary>
/// <c>ActionText::PlainTextConversion.node_to_plain_text</c>: a bottom-up reduction of the tree,
/// keyed on each node's Nokogiri name (<c>plain_text_for_#{node.name}_node</c>), so a text node and
/// an SVG <c>&lt;text&gt;</c> element convert alike, whatever namespace an element is in.
/// </summary>
public static class PlainTextConversion
{
    public static string NodeToPlainText(HtmlNode node) => RemoveTrailingNewlines(Reduce(node));

    /// <summary>
    /// <c>BottomUpReducer#reduce</c>: every node's value from its children's, without recursing,
    /// so arbitrarily deep trees convert.
    /// </summary>
    static string Reduce(HtmlNode root)
    {
        var values = new Dictionary<HtmlNode, string>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<HtmlNode>([root]);
        var processing = new Stack<HtmlNode>();
        while (pending.TryPop(out var node))
        {
            processing.Push(node);
            if (node is HtmlParentNode parent)
            {
                foreach (var child in parent.Children)
                {
                    pending.Push(child);
                }
            }
        }
        while (processing.TryPop(out var node))
        {
            var childValues = node is HtmlParentNode parent ? parent.Children.Select(child => values[child]).ToList() : [];
            values[node] = PlainTextFor(node, childValues);
        }
        return values[root];
    }

    static string PlainTextFor(HtmlNode node, List<string> childValues) => node.NodeName switch
    {
        "script" or "style" or "unsupported" => "",
        "h1" or "p" => PlainTextForBlock(childValues),
        "ul" or "ol" => ListDepth(node) > 0 ? "\n" + PlainTextForBlock(childValues) : PlainTextForBlock(childValues),
        "br" => "\n",
        "text" => RemoveTrailingNewlines(node.TextContent),
        "div" => RemoveTrailingNewlines(string.Concat(childValues)) + "\n",
        "figcaption" => $"[{RemoveTrailingNewlines(string.Concat(childValues))}]",
        "blockquote" => PlainTextForBlockquote(childValues),
        "li" => PlainTextForLi(node, childValues),
        _ => string.Concat(childValues),
    };

    static string PlainTextForBlock(List<string> childValues) => RemoveTrailingNewlines(string.Concat(childValues)) + "\n\n";

    // text.insert(text.rindex(/\S/) + 1, "”"), then text.insert(text.index(/\S/), "“")
    static string PlainTextForBlockquote(List<string> childValues)
    {
        var text = PlainTextForBlock(childValues);
        if (RubyText.IsBlank(text))
        {
            return "“”";
        }
        var quoted = new StringBuilder(text);
        quoted.Insert(LastIndexOfNonSpace(text) + 1, '”');
        quoted.Insert(IndexOfNonSpace(text), '“');
        return quoted.ToString();
    }

    static string PlainTextForLi(HtmlNode node, List<string> childValues)
    {
        var depth = ListDepth(node);
        var indentation = depth > 1 ? string.Concat(Enumerable.Repeat("  ", depth - 1)) : "";
        return $"{indentation}{BulletForLi(node)} {RemoveTrailingNewlines(string.Concat(childValues))}\n";
    }

    static string BulletForLi(HtmlNode node)
    {
        if (Ancestors(node).Select(a => a.NodeName).FirstOrDefault(IsList) != "ol")
        {
            return "•";
        }
        // node.parent.elements.index(node)
        var index = node.Parent!.Elements.ToList().IndexOf((HtmlElement)node);
        return $"{index + 1}.";
    }

    static int ListDepth(HtmlNode node) => Ancestors(node).Count(a => IsList(a.NodeName));

    static bool IsList(string name) => name is "ul" or "ol";

    static IEnumerable<HtmlNode> Ancestors(HtmlNode node)
    {
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            yield return ancestor;
        }
    }

    /// <summary>
    /// <c>String#chomp("")</c>: every trailing <c>\n</c>, each with a <c>\r</c> before it, but not a
    /// lone trailing <c>\r</c>.
    /// </summary>
    public static string RemoveTrailingNewlines(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '\n')
        {
            end--;
            if (end > 0 && text[end - 1] == '\r')
            {
                end--;
            }
        }
        return text[..end];
    }

    // Ruby's \s and \S are ASCII-only
    static bool IsRubySpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    static int IndexOfNonSpace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsRubySpace(text[i]))
            {
                return i;
            }
        }
        return -1;
    }

    static int LastIndexOfNonSpace(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!IsRubySpace(text[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
