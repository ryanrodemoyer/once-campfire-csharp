namespace Campfire.RichText.Sanitize;

/// <summary>
/// A tag and attribute allowlist, as passed to Rails HTML sanitizers.
/// Matches Rails::HTML5::SafeListSanitizer and ContentFilters (content_filters.rb).
/// </summary>
public sealed class SafeList
{
    public static readonly string[] DefaultAllowedTags =
    [
        "a", "abbr", "acronym", "address", "b", "big", "blockquote", "br", "cite", "code", "dd", "del", "dfn", "div", "dl", "dt", "em", "h1",
        "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "mark", "ol", "p", "pre", "samp", "small", "span", "strong", "sub",
        "sup", "time", "tt", "ul", "var"
    ];

    public static readonly string[] DefaultAllowedAttributes =
    [
        "abbr", "alt", "cite", "class", "datetime", "height", "href", "lang", "name", "src", "title", "width", "xml:lang"
    ];

    public static readonly string[] EditorFormattingTags =
    [
        "s", "u", "mark", "table", "thead", "tbody", "tfoot", "tr", "th", "td"
    ];

    public static readonly string[] EditorFormattingAttributes =
    [
        "data-language"
    ];

    public static readonly string[] AttachmentAttributes =
    [
        "sgid", "content-type", "url", "href", "filename", "filesize", "width", "height", "previewable", "presentation", "caption", "content"
    ];

    public HashSet<string> Tags { get; }
    public HashSet<string> Attributes { get; }

    public SafeList(IEnumerable<string> tags, IEnumerable<string> attributes)
    {
        Tags = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        Attributes = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
    }

    public bool AllowsTag(string name) => Tags.Contains(name);

    public bool AllowsAttribute(string name) => Attributes.Contains(name);

    public static SafeList Defaults { get; } = new(DefaultAllowedTags, DefaultAllowedAttributes);

    public static string[] SanitizeTagsAllowedTags { get; } = CreateSanitizeTagsAllowedTags();

    public static SafeList ActionText { get; } = CreateActionText();

    public static SafeList ContentFilter { get; } = CreateContentFilter();

    public static SafeList AutoLink { get; } = CreateAutoLink();

    static string[] CreateSanitizeTagsAllowedTags()
    {
        var tags = new List<string>
        {
            "a", "abbr", "acronym", "address", "b", "big", "blockquote", "br", "cite", "code", "dd", "del", "dfn", "div", "dl", "dt", "em",
            "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "ins", "kbd", "li", "ol", "p", "pre", "samp", "small", "span", "strong", "sub",
            "sup", "time", "tt", "ul", "var"
        };
        tags.AddRange(EditorFormattingTags);
        tags.AddRange(["action-text-attachment", "figure", "figcaption"]);
        return [.. tags];
    }

    static SafeList CreateActionText()
    {
        var tags = new List<string>(DefaultAllowedTags);
        tags.AddRange(["action-text-attachment", "figure", "figcaption"]);
        tags.AddRange(["video", "audio", "source", "embed", "table", "tbody", "tr", "th", "td"]);
        foreach (var tag in EditorFormattingTags)
        {
            if (!tags.Contains(tag)) tags.Add(tag);
        }

        var attributes = new List<string>(DefaultAllowedAttributes);
        attributes.AddRange(AttachmentAttributes);
        attributes.AddRange(["controls", "poster", "data-language", "style", "value", "start"]);
        foreach (var attr in EditorFormattingAttributes)
        {
            if (!attributes.Contains(attr)) attributes.Add(attr);
        }

        return new SafeList(tags, attributes);
    }

    static SafeList CreateContentFilter()
    {
        var attributes = new List<string>(ActionText.Attributes);
        if (!attributes.Contains("class", StringComparer.OrdinalIgnoreCase))
        {
            attributes.Add("class");
        }
        return new SafeList(SanitizeTagsAllowedTags, attributes);
    }

    static SafeList CreateAutoLink()
    {
        var tags = new List<string>(DefaultAllowedTags);
        foreach (var tag in EditorFormattingTags)
        {
            if (!tags.Contains(tag)) tags.Add(tag);
        }

        var attributes = new List<string>(DefaultAllowedAttributes);
        attributes.AddRange(EditorFormattingAttributes);

        return new SafeList(tags, attributes);
    }
}
