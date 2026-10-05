using Campfire.RichText.Html.Gumbo;

namespace Campfire.RichText.Html;

/// <summary>
/// <c>Nokogiri::HTML5</c> parsing (nokogiri 1.19.4): Gumbo's parse, turned into the tree
/// nokogiri's ext/nokogiri/gumbo.c builds from it.
/// </summary>
public static class HtmlParser
{
    /// <summary>
    /// <c>HTML5::Document#fragment</c> (in a <c>body</c> context) or <c>Node#fragment</c> (in the
    /// node's context, see <see cref="FragmentContext.For"/>).
    /// </summary>
    /// <exception cref="HtmlParseException">The input exceeds Gumbo's tree depth or attribute limits.</exception>
    public static HtmlFragment ParseFragment(string html, FragmentContext? context = null, HtmlParseOptions? options = null)
    {
        var result = TreeBuilder.Parse(html, options ?? HtmlParseOptions.Default, context ?? FragmentContext.Body);
        ThrowOnError(result);
        var fragment = new HtmlFragment();
        foreach (var child in result.Root!.Children.ToArray())
        {
            fragment.AppendChild(child);
        }
        MergeAdjacentText(fragment);
        return fragment;
    }

    /// <summary><c>Nokogiri::HTML5.parse</c>.</summary>
    /// <exception cref="HtmlParseException">The input exceeds Gumbo's tree depth or attribute limits.</exception>
    public static HtmlDocument ParseDocument(string html, HtmlParseOptions? options = null)
    {
        var result = TreeBuilder.Parse(html, options ?? HtmlParseOptions.Default, null);
        ThrowOnError(result);
        var document = result.Document;
        // libxml2 keeps the doctype as the document's first child
        if (result.Doctype is not null)
        {
            document.InsertAt(0, result.Doctype);
        }
        MergeAdjacentText(document);
        return document;
    }

    static void ThrowOnError(TreeBuilder.Result result)
    {
        if (result.Error is not null)
        {
            throw new HtmlParseException(result.Error);
        }
    }

    // xmlAddChild merges a text node into a text node just before it
    static void MergeAdjacentText(HtmlParentNode root)
    {
        var pending = new Stack<HtmlParentNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var children = pending.Pop().ChildList;
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is HtmlText text)
                {
                    while (i + 1 < children.Count && children[i + 1] is HtmlText next)
                    {
                        text.Data += next.Data;
                        next.Remove();
                    }
                }
                else if (children[i] is HtmlParentNode parent)
                {
                    pending.Push(parent);
                }
            }
        }
    }
}

/// <summary>The options Nokogiri passes Gumbo. A negative limit means none.</summary>
public sealed record HtmlParseOptions
{
    public static HtmlParseOptions Default { get; } = new();

    /// <summary><c>Nokogiri::Gumbo::DEFAULT_MAX_TREE_DEPTH</c>.</summary>
    public int MaxTreeDepth { get; init; } = 400;

    /// <summary><c>Nokogiri::Gumbo::DEFAULT_MAX_ATTRIBUTES</c>.</summary>
    public int MaxAttributes { get; init; } = 400;
}

/// <summary>The element a fragment is parsed in, as nokogiri's gumbo.c describes it to Gumbo.</summary>
public sealed record FragmentContext(
    string Name,
    HtmlNamespace Namespace = HtmlNamespace.Html,
    bool HasFormAncestor = false,
    string? Encoding = null,
    HtmlQuirksMode QuirksMode = HtmlQuirksMode.NoQuirks)
{
    /// <summary>What <c>HTML5::Document#fragment</c> uses.</summary>
    public static FragmentContext Body { get; } = new("body");

    /// <summary>
    /// The context <c>Node#fragment</c> gives Gumbo, which <c>inner_html=</c> and <c>replace</c>
    /// use: the node's name and namespace, whether it or an ancestor is an HTML form, a MathML
    /// annotation-xml's encoding, and the quirks mode of a parsed document's doctype.
    /// </summary>
    public static FragmentContext For(HtmlNode node)
    {
        if (node is not HtmlElement element)
        {
            // Gumbo sees an unknown HTML tag, which parses like body
            return Body;
        }

        var hasFormAncestor = false;
        for (HtmlNode? ancestor = element; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is HtmlElement { Namespace: HtmlNamespace.Html, Tag: "form" })
            {
                hasFormAncestor = true;
                break;
            }
        }

        var encoding = element.Namespace == HtmlNamespace.MathMl && element.Tag == "annotation-xml"
            ? element.GetAttribute("encoding")
            : null;
        return new FragmentContext(element.Name, element.Namespace, hasFormAncestor, encoding, DocumentQuirksMode(element));
    }

    // A parsed document's quirks mode comes from its doctype (none: quirks); anything else is no-quirks
    static HtmlQuirksMode DocumentQuirksMode(HtmlNode node)
    {
        var top = node;
        while (top.Parent is not null)
        {
            top = top.Parent;
        }
        if (top is not HtmlDocument document)
        {
            return HtmlQuirksMode.NoQuirks;
        }
        if (document.FirstChild is not HtmlDoctype doctype)
        {
            return HtmlQuirksMode.Quirks;
        }
        return TreeBuilder.ComputeQuirksMode(
            doctype.Name,
            doctype.PublicId.Length > 0 ? doctype.PublicId : null,
            doctype.SystemId.Length > 0 ? doctype.SystemId : null);
    }
}

/// <summary>
/// The <c>ArgumentError</c> Nokogiri raises when Gumbo stops at one of its limits; the message is
/// Gumbo's status string.
/// </summary>
public sealed class HtmlParseException : Exception
{
    public const string TreeTooDeep = "Document tree depth limit exceeded";
    public const string TooManyAttributes = "Attributes per element limit exceeded";

    public HtmlParseException()
    {
    }

    public HtmlParseException(string message)
        : base(message)
    {
    }

    public HtmlParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
