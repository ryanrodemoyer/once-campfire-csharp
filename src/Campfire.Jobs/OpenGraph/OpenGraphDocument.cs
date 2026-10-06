using System.Collections.Immutable;

namespace Campfire.Jobs.OpenGraph;

/// <summary><c>Opengraph::Document</c> (reference/app/models/opengraph/document.rb).</summary>
public static class OpenGraphDocument
{
    /// <summary><c>Opengraph::Metadata::ATTRIBUTES</c>, in the order <c>Hash#slice</c> returns them.</summary>
    public static readonly ImmutableArray<string> Attributes = ["title", "url", "image", "description"];

    /// <summary>
    /// <c>opengraph_attributes</c>: from each <c>meta</c> whose <c>property</c> or <c>name</c>
    /// starts with "og:", the key is that attribute (<c>property</c> when present) with every
    /// "og:" removed, and the value its non-blank <c>content</c>. Later tags win. Without a meta
    /// charset, non-ASCII characters are dropped
    /// (<c>content.encode("UTF-8", "binary", invalid: :replace, undef: :replace, replace: "")</c>).
    /// A missing body (<c>Nokogiri::HTML(nil)</c>) has no tags.
    /// </summary>
    public static List<KeyValuePair<string, string>> OpenGraphAttributes(ReadOnlySpan<byte> body)
    {
        var metas = LegacyHtml.MetaElements(LegacyHtml.Decode(body));
        var hasMetaEncoding = LegacyHtml.MetaEncoding(metas) is not null;

        // Only the ATTRIBUTES keys are sliced out, so only they are kept.
        var found = new string?[Attributes.Length];
        foreach (var meta in metas.Where(IsOpenGraphTag))
        {
            var key = meta.HasAttribute("property") ? "property" : "name";
            var index = Attributes.IndexOf((meta[key] ?? "").Replace("og:", "", StringComparison.Ordinal));
            if (index < 0 || meta["content"] is not { } content || IsBlank(content))
            {
                continue;
            }
            found[index] = hasMetaEncoding ? content : new string([.. content.Where(char.IsAscii)]);
        }

        return [.. Attributes.Zip(found).Where(pair => pair.Second is not null).Select(pair => KeyValuePair.Create(pair.First, pair.Second!))];
    }

    /// <summary><c>//*/meta[starts-with(@property, "og:") or starts-with(@name, "og:")]</c></summary>
    static bool IsOpenGraphTag(MetaElement meta) =>
        meta["property"]?.StartsWith("og:", StringComparison.Ordinal) == true || meta["name"]?.StartsWith("og:", StringComparison.Ordinal) == true;

    /// <summary><c>String#blank?</c>: empty or only (Unicode) whitespace.</summary>
    public static bool IsBlank(string? value) => value is null || value.All(char.IsWhiteSpace);
}
