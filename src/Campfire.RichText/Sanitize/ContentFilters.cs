using System.Text;
using Campfire.RichText.Html;

namespace Campfire.RichText.Sanitize;

/// <summary>
/// TextMessagePresentationFilters:
/// 1. RemoveSoloUnfurledLinkText
/// 2. SanitizeTags (drop disallowed elements with contents)
/// 3. SanitizeAttributes (scrub attributes with SafeList.ContentFilter)
/// </summary>
public static class ContentFilters
{
    private static readonly string[] TwitterDomains = ["x.com", "twitter.com"];

    private static readonly System.Text.Json.JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip
    };

    public static string ApplyTextMessagePresentationFilters(string html, string? requestHost = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var fragment = HtmlParser.ParseFragment(html.Trim());
        CanonicalizeContent(fragment);
        ApplyRemoveSoloUnfurledLinkText(fragment, requestHost);
        ApplySanitizeTags(fragment);
        var sanitized = SafeListSanitizer.Sanitize(fragment.ToHtml(), SafeList.ContentFilter);
        return sanitized.Trim();
    }

    public static void CanonicalizeContent(HtmlFragment fragment)
    {
        // 1. Convert trix attachments: [data-trix-attachment]
        var trixNodes = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.HasAttribute("data-trix-attachment"))
            .ToList();

        foreach (var node in trixNodes)
        {
            var trixJson = node.GetAttribute("data-trix-attachment");
            var trixAttrsJson = node.GetAttribute("data-trix-attributes");

            var map = new Dictionary<string, string>();
            foreach (var jsonStr in new[] { trixJson, trixAttrsJson })
            {
                if (string.IsNullOrWhiteSpace(jsonStr)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonStr, JsonOptions);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        throw new InvalidOperationException("undefined method 'merge' for an instance of Array");
                    }
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var val = prop.Value.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.Null => string.Empty,
                                System.Text.Json.JsonValueKind.True => "true",
                                System.Text.Json.JsonValueKind.False => "false",
                                System.Text.Json.JsonValueKind.Number => prop.Value.GetRawText(),
                                System.Text.Json.JsonValueKind.String => prop.Value.GetString()!,
                                _ => prop.Value.GetRawText()
                            };
                            map[prop.Name] = val;
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Malformed JSON is treated as empty
                }
            }

            var attPairs = new List<HtmlAttr>();
            ReadOnlySpan<(string Trix, string Dashed)> trixAttrs =
            [
                ("sgid", "sgid"),
                ("contentType", "content-type"),
                ("url", "url"),
                ("href", "href"),
                ("filename", "filename"),
                ("filesize", "filesize"),
                ("width", "width"),
                ("height", "height"),
                ("previewable", "previewable"),
                ("content", "content"),
                ("caption", "caption"),
                ("presentation", "presentation"),
            ];

            foreach (var (trix, dashed) in trixAttrs)
            {
                if (map.TryGetValue(trix, out var val))
                {
                    attPairs.Add(new HtmlAttr(dashed, val));
                }
            }

            if (attPairs.Count == 0)
            {
                node.Remove();
            }
            else
            {
                var replacement = new HtmlElement("action-text-attachment", HtmlNamespace.Html);
                foreach (var attr in attPairs)
                {
                    replacement.SetAttribute(attr.Name, attr.Value);
                }
                node.Parent?.ReplaceChild(node, [replacement]);
            }
        }

        // 2. Clear inner HTML of all action-text-attachment nodes
        var attachments = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.IsHtml("action-text-attachment"))
            .ToList();

        foreach (var att in attachments)
        {
            att.RemoveAllChildren();
        }

        // 3. Attachment gallery canonicalization:
        // div elements containing adjacent gallery attachments where all children are either
        // gallery attachments or whitespace text (newlines/spaces) are stripped of their attributes.
        static bool IsGalleryAttachment(HtmlNode node) =>
            node is HtmlElement e && e.IsHtml("action-text-attachment") && e.GetAttribute("presentation") == "gallery";

        var divs = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.IsHtml("div"))
            .ToList();

        foreach (var div in divs)
        {
            var children = div.Children.ToList();
            if (children.Count == 0) continue;

            var allChildrenValid = children.All(c =>
                IsGalleryAttachment(c) ||
                (c is HtmlText t && t.Data.All(ch => ch == '\n' || ch == ' ')));

            if (!allChildrenValid) continue;

            var galleryCount = children.Count(IsGalleryAttachment);
            if (galleryCount >= 2)
            {
                div.AttributeList.Clear();
            }
        }
    }

    public static void ApplyRemoveSoloUnfurledLinkText(HtmlFragment fragment, string? requestHost)
    {
        var unfurledLinks = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.IsHtml("action-text-attachment") &&
                        e.GetAttribute("content-type") == "application/vnd.actiontext.opengraph-embed")
            .ToList();

        if (unfurledLinks.Count != 1)
        {
            return;
        }

        var unfurl = unfurledLinks[0];
        var soloUrl = GetSoloUnfurledUrl(unfurl, requestHost);
        if (soloUrl is null)
        {
            return;
        }

        var plainText = ToPlainText(fragment);
        var normSolo = NormalizeTweetUrl(soloUrl);
        var normPlain = NormalizeTweetUrl(plainText);

        if (!string.Equals(normSolo, normPlain, StringComparison.Ordinal))
        {
            return;
        }

        var isTrixBody = fragment.Descendants().OfType<HtmlElement>().Any(e => e.IsHtml("div"));
        if (isTrixBody)
        {
            var unfurlHtml = unfurl.ToHtml();
            var divs = fragment.Descendants().OfType<HtmlElement>().Where(e => e.IsHtml("div")).ToList();
            foreach (var div in divs)
            {
                div.RemoveAllChildren();
                var parsedUnfurl = HtmlParser.ParseFragment(unfurlHtml);
                foreach (var child in parsedUnfurl.Children.ToArray())
                {
                    div.AppendChild(child);
                }
            }
        }
        else
        {
            var paragraphs = fragment.Descendants().OfType<HtmlElement>().Where(e => e.IsHtml("p")).ToList();
            foreach (var p in paragraphs)
            {
                var hasAttachment = p.Descendants().OfType<HtmlElement>().Any(e => e.IsHtml("action-text-attachment"));
                if (!hasAttachment)
                {
                    p.Remove();
                }
            }
        }
    }

    public static void ApplySanitizeTags(HtmlFragment fragment)
    {
        var allowed = new HashSet<string>(SafeList.SanitizeTagsAllowedTags, StringComparer.OrdinalIgnoreCase);
        var elements = fragment.Descendants().OfType<HtmlElement>().ToList();
        foreach (var element in elements)
        {
            if (element.Parent is not null && !allowed.Contains(element.Tag))
            {
                element.Remove();
            }
        }
    }

    static string? GetSoloUnfurledUrl(HtmlElement node, string? requestHost)
    {
        var filename = node.GetAttribute("filename");
        if (!string.IsNullOrEmpty(filename))
        {
            return OpengraphEmbedUrl.WebUrl(node.GetAttribute("href"), requestHost);
        }

        var content = node.GetAttribute("content");
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var fragment = HtmlParser.ParseFragment(content);
        var title = fragment.Descendants().OfType<HtmlElement>()
            .FirstOrDefault(e => HasClass(e, "og-embed__title"));

        var link = title?.Descendants().OfType<HtmlElement>()
            .FirstOrDefault(e => e.IsHtml("a"));

        var href = link?.GetAttribute("href");
        return OpengraphEmbedUrl.WebUrl(href, requestHost);
    }

    static bool HasClass(HtmlElement element, string className)
    {
        var cls = element.GetAttribute("class");
        if (cls is null) return false;
        var parts = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains(className, StringComparer.Ordinal);
    }

    public static string NormalizeTweetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url ?? string.Empty;
        }

        var trimmed = url.Trim();
        var isTwitter = TwitterDomains.Any(d => trimmed.Contains(d, StringComparison.OrdinalIgnoreCase));
        if (!isTwitter)
        {
            return url;
        }

        try
        {
            var uri = RubyUri.Parse(url);
            if (string.Equals(uri.Host, "x.com", StringComparison.OrdinalIgnoreCase))
            {
                uri.Host = "twitter.com";
            }
            uri.Query = null;
            return uri.ToUriString();
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// Computes plain text conversion as ActionText does, treating attachment nodes
    /// as their attachable plain text representations.
    /// </summary>
    public static string ToPlainText(HtmlFragment fragment)
    {
        var copy = (HtmlFragment)fragment.Clone();

        // Attachments: opengraph embeds have empty plain text representation
        var attachments = copy.Descendants().OfType<HtmlElement>()
            .Where(e => e.IsHtml("action-text-attachment"))
            .ToList();

        foreach (var att in attachments)
        {
            att.Remove();
        }

        return ChompNewlines(PlainTextFor(copy));
    }

    static string PlainTextFor(HtmlNode node)
    {
        var childValues = (node is HtmlParentNode parent
            ? parent.Children.Select(PlainTextFor).ToArray()
            : []);

        var name = node is HtmlElement el ? el.Tag : node.NodeName;
        switch (name)
        {
            case "script" or "style" or "unsupported":
                return string.Empty;
            case "h1" or "p":
                return PlainTextForBlock(childValues);
            case "ul" or "ol":
                var listText = PlainTextForBlock(childValues);
                return ListDepth(node) > 0 ? $"\n{listText}" : listText;
            case "br":
                return "\n";
            case "text" or "#text":
                return ChompNewlines(node is HtmlCharacterData cd ? cd.Data : node.TextContent);
            case "div":
                return $"{ChompNewlines(string.Concat(childValues))}\n";
            case "figcaption":
                return $"[{ChompNewlines(string.Concat(childValues))}]";
            case "blockquote":
                var bText = PlainTextForBlock(childValues);
                if (string.IsNullOrWhiteSpace(bText))
                {
                    return "“”";
                }
                var sb = new StringBuilder(bText);
                var lastNonSpace = -1;
                for (var i = sb.Length - 1; i >= 0; i--)
                {
                    if (!char.IsWhiteSpace(sb[i]))
                    {
                        lastNonSpace = i;
                        break;
                    }
                }
                if (lastNonSpace >= 0)
                {
                    sb.Insert(lastNonSpace + 1, '”');
                }
                var firstNonSpace = -1;
                for (var i = 0; i < sb.Length; i++)
                {
                    if (!char.IsWhiteSpace(sb[i]))
                    {
                        firstNonSpace = i;
                        break;
                    }
                }
                if (firstNonSpace >= 0)
                {
                    sb.Insert(firstNonSpace, '“');
                }
                return sb.ToString();
            case "li":
                var bullet = BulletForLi(node);
                var liText = ChompNewlines(string.Concat(childValues));
                var depth = ListDepth(node);
                var indentation = depth > 1 ? new string(' ', (depth - 1) * 2) : string.Empty;
                return $"{indentation}{bullet} {liText}\n";
            default:
                return string.Concat(childValues);
        }
    }

    static string PlainTextForBlock(string[] childValues) =>
        $"{ChompNewlines(string.Concat(childValues))}\n\n";

    static bool IsList(string name) => name is "ul" or "ol";

    static int ListDepth(HtmlNode node)
    {
        var count = 0;
        var current = node.Parent;
        while (current is not null)
        {
            if (current is HtmlElement el && IsList(el.Tag))
            {
                count++;
            }
            current = current.Parent;
        }
        return count;
    }

    static string BulletForLi(HtmlNode node)
    {
        var current = node.Parent;
        HtmlElement? listElement = null;
        while (current is not null)
        {
            if (current is HtmlElement el && IsList(el.Tag))
            {
                listElement = el;
                break;
            }
            current = current.Parent;
        }

        if (listElement?.IsHtml("ol") == true)
        {
            var parent = node.Parent;
            var index = parent?.Elements.ToList().IndexOf((HtmlElement)node) ?? 0;
            return $"{index + 1}.";
        }

        return "•";
    }

    static string ChompNewlines(string s)
    {
        var end = s.Length;
        while (end > 0 && (s[end - 1] == '\r' || s[end - 1] == '\n'))
        {
            end--;
        }
        return s[..end];
    }
}
