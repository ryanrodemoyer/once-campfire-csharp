namespace Campfire.RichText.Html;

// The tree Nokogiri hands Action Text and Loofah for an HTML5 document: libxml2 nodes built from
// Gumbo's parse by nokogiri's ext/nokogiri/gumbo.c (build_tree). Element names are as Gumbo
// produced them (lowercase, or SVG's camelCase), templates keep their contents as children,
// adjacent text is merged as xmlAddChild merges it, and foreign attributes carry their prefix.

public enum HtmlNamespace
{
    Html,
    Svg,
    MathMl,
}

public abstract class HtmlNode
{
    public HtmlParentNode? Parent { get; internal set; }

    /// <summary>Nokogiri's <c>Node#name</c>: the element name, or "text", "comment" and so on.</summary>
    public abstract string NodeName { get; }

    /// <summary>The node's text as libxml2's <c>xmlNodeGetContent</c> computes it.</summary>
    public abstract string TextContent { get; }

    /// <summary>Detaches the node from its parent (Nokogiri's <c>Node#unlink</c>).</summary>
    public void Remove() => Parent?.RemoveChild(this);

    /// <summary>A deep copy with no parent.</summary>
    public abstract HtmlNode Clone();

    /// <summary>Nokogiri's <c>to_html</c> for an HTML5 document (see <see cref="HtmlSerializer"/>).</summary>
    public string ToHtml() => HtmlSerializer.Serialize(this);

    public override string ToString() => ToHtml();
}

public abstract class HtmlParentNode : HtmlNode
{
    readonly List<HtmlNode> children = [];

    public IReadOnlyList<HtmlNode> Children => children;

    public HtmlNode? FirstChild => children.Count > 0 ? children[0] : null;

    public HtmlNode? LastChild => children.Count > 0 ? children[^1] : null;

    public IEnumerable<HtmlElement> Elements => children.OfType<HtmlElement>();

    public override string TextContent
    {
        get
        {
            var text = new System.Text.StringBuilder();
            foreach (var node in Descendants())
            {
                if (node is HtmlCharacterData data and not HtmlComment)
                {
                    text.Append(data.Data);
                }
            }
            return text.ToString();
        }
    }

    /// <summary>All descendants in document order, excluding this node.</summary>
    public IEnumerable<HtmlNode> Descendants()
    {
        var stack = new Stack<(HtmlParentNode Parent, int Index)>();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            var (parent, index) = stack.Pop();
            if (index >= parent.children.Count)
            {
                continue;
            }
            var child = parent.children[index];
            stack.Push((parent, index + 1));
            yield return child;
            if (child is HtmlParentNode childParent)
            {
                stack.Push((childParent, 0));
            }
        }
    }

    public void AppendChild(HtmlNode child) => InsertAt(children.Count, child);

    public void InsertBefore(HtmlNode child, HtmlNode reference)
    {
        if (reference.Parent != this)
        {
            throw new ArgumentException("The reference node is not a child of this node.", nameof(reference));
        }
        child.Remove();
        InsertAt(IndexOf(reference), child);
    }

    public void InsertAt(int index, HtmlNode child)
    {
        child.Remove();
        children.Insert(index, child);
        child.Parent = this;
    }

    public void RemoveChild(HtmlNode child)
    {
        var index = IndexOf(child);
        if (index < 0)
        {
            throw new ArgumentException("Not a child of this node.", nameof(child));
        }
        RemoveAt(index);
    }

    public void RemoveAt(int index)
    {
        var child = children[index];
        children.RemoveAt(index);
        child.Parent = null;
    }

    /// <summary>Replaces <paramref name="child"/> with <paramref name="replacements"/>, in order.</summary>
    public void ReplaceChild(HtmlNode child, IEnumerable<HtmlNode> replacements)
    {
        var detached = replacements.ToList();
        foreach (var node in detached)
        {
            InsertBefore(node, child);
        }
        RemoveChild(child);
    }

    public void RemoveAllChildren()
    {
        foreach (var child in children)
        {
            child.Parent = null;
        }
        children.Clear();
    }

    /// <summary>
    /// The child's index, searching from the end: the parser inserts before an open table, which is
    /// usually its parent's last child.
    /// </summary>
    public int IndexOf(HtmlNode child) => child.Parent == this ? children.LastIndexOf(child) : -1;

    internal List<HtmlNode> ChildList => children;

    protected void CloneChildrenInto(HtmlParentNode copy)
    {
        foreach (var child in children)
        {
            copy.AppendChild(child.Clone());
        }
    }
}

/// <summary>A document, as <c>Nokogiri::HTML5.parse</c> returns it.</summary>
public sealed class HtmlDocument : HtmlParentNode
{
    public HtmlQuirksMode QuirksMode { get; internal set; }

    public override string NodeName => "document";

    public override HtmlNode Clone()
    {
        var copy = new HtmlDocument { QuirksMode = QuirksMode };
        CloneChildrenInto(copy);
        return copy;
    }
}

/// <summary>A document fragment, as <c>Nokogiri::HTML5::DocumentFragment</c> holds it.</summary>
public sealed class HtmlFragment : HtmlParentNode
{
    public override string NodeName => "#document-fragment";

    public override HtmlNode Clone()
    {
        var copy = new HtmlFragment();
        CloneChildrenInto(copy);
        return copy;
    }
}

public sealed class HtmlElement : HtmlParentNode
{
    public HtmlElement(string name, HtmlNamespace ns = HtmlNamespace.Html)
        : this(name, Gumbo.Tags.AsciiToLower(name), ns, [])
    {
    }

    internal HtmlElement(string name, string tag, HtmlNamespace ns, List<HtmlAttr> attributes)
    {
        Name = name;
        Tag = tag;
        Namespace = ns;
        AttributeList = attributes;
    }

    /// <summary>The element name as Nokogiri reports and serializes it ("p", "clipPath").</summary>
    public string Name { get; }

    public HtmlNamespace Namespace { get; }

    /// <summary>Attributes in source order.</summary>
    public IReadOnlyList<HtmlAttr> Attributes => AttributeList;

    public override string NodeName => Name;

    /// <summary>The lowercase name Gumbo matches tags by.</summary>
    internal string Tag { get; }

    internal List<HtmlAttr> AttributeList { get; set; }

    public bool IsHtml(string tag) => Namespace == HtmlNamespace.Html && Tag == tag;

    /// <summary>Nokogiri's <c>node[name]</c>: the value of the attribute with this qualified name.</summary>
    public string? GetAttribute(string qualifiedName) => FindAttribute(qualifiedName)?.Value;

    public bool HasAttribute(string qualifiedName) => FindAttribute(qualifiedName) is not null;

    public HtmlAttr? FindAttribute(string qualifiedName)
    {
        foreach (var attribute in AttributeList)
        {
            if (attribute.HasQualifiedName(qualifiedName))
            {
                return attribute;
            }
        }
        return null;
    }

    /// <summary>Nokogiri's <c>node[name] = value</c>: updates in place, or appends a new attribute.</summary>
    public void SetAttribute(string qualifiedName, string value)
    {
        var existing = FindAttribute(qualifiedName);
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            AttributeList.Add(new HtmlAttr(qualifiedName, value));
        }
    }

    /// <summary>Removes the attribute with this qualified name and returns its value.</summary>
    public string? RemoveAttribute(string qualifiedName)
    {
        for (var i = 0; i < AttributeList.Count; i++)
        {
            if (AttributeList[i].HasQualifiedName(qualifiedName))
            {
                var value = AttributeList[i].Value;
                AttributeList.RemoveAt(i);
                return value;
            }
        }
        return null;
    }

    public void RemoveAttribute(HtmlAttr attribute) => AttributeList.Remove(attribute);

    public override HtmlNode Clone()
    {
        var copy = new HtmlElement(Name, Tag, Namespace, AttributeList.ConvertAll(a => a.Clone()));
        CloneChildrenInto(copy);
        return copy;
    }
}

/// <summary>
/// An attribute. Foreign attributes Gumbo puts in a namespace (<c>xlink:href</c>, <c>xml:lang</c>,
/// <c>xmlns:xlink</c>) have a <see cref="Prefix"/>; every other name, colons included, is plain.
/// </summary>
public sealed class HtmlAttr
{
    public HtmlAttr(string name, string value)
        : this(null, name, value)
    {
    }

    public HtmlAttr(string? prefix, string name, string value)
    {
        Prefix = prefix;
        Name = name;
        Value = value;
    }

    /// <summary>"xlink", "xml" or "xmlns" for a namespaced foreign attribute, otherwise null.</summary>
    public string? Prefix { get; }

    /// <summary>The local name (libxml2's <c>attr-&gt;name</c>).</summary>
    public string Name { get; internal set; }

    public string Value { get; set; }

    /// <summary>The name as Nokogiri serializes it: "xlink:href", "href", and "xmlns" for xmlns.</summary>
    public string QualifiedName => Prefix is null || (Prefix == "xmlns" && Name == "xmlns") ? Name : Prefix + ":" + Name;

    public bool HasQualifiedName(string qualifiedName) =>
        Prefix is null ? Name == qualifiedName : QualifiedName == qualifiedName;

    public HtmlAttr Clone() => new(Prefix, Name, Value);

    public override string ToString() => $"{QualifiedName}=\"{Value}\"";
}

public abstract class HtmlCharacterData(string data) : HtmlNode
{
    public string Data { get; set; } = data;

    public override string TextContent => Data;
}

public sealed class HtmlText(string data) : HtmlCharacterData(data)
{
    public override string NodeName => "text";

    public override HtmlNode Clone() => new HtmlText(Data);
}

/// <summary>A CDATA section, which Gumbo only produces inside SVG and MathML.</summary>
public sealed class HtmlCData(string data) : HtmlCharacterData(data)
{
    public override string NodeName => "#cdata-section";

    public override HtmlNode Clone() => new HtmlCData(Data);
}

public sealed class HtmlComment(string data) : HtmlCharacterData(data)
{
    public override string NodeName => "comment";

    public override HtmlNode Clone() => new HtmlComment(Data);
}

/// <summary>A document's doctype (libxml2's internal subset).</summary>
public sealed class HtmlDoctype(string name, string publicId, string systemId) : HtmlNode
{
    public string Name { get; } = name;

    public string PublicId { get; } = publicId;

    public string SystemId { get; } = systemId;

    public override string NodeName => Name;

    public override string TextContent => "";

    public override HtmlNode Clone() => new HtmlDoctype(Name, PublicId, SystemId);
}

public enum HtmlQuirksMode
{
    NoQuirks,
    Quirks,
    LimitedQuirks,
}
