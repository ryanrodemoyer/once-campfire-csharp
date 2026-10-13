using System.Text;
using Campfire.RailsCompat.Ruby;
using Campfire.RichText.Attachments;
using Campfire.RichText.Html;
using Campfire.RichText.PlainText;

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

    /// <summary>
    /// <c>ContentFilters::TextMessagePresentationFilters.apply</c>. When <paramref name="context"/>
    /// is set, <c>RemoveSoloUnfurledLinkText#applicable?</c> has evaluated <c>content.to_plain_text</c>
    /// (both sides of <c>==</c>), which resolves every attachment. Callers that only compare
    /// sanitized markup omit it.
    /// </summary>
    public static string ApplyTextMessagePresentationFilters(string html, string? requestHost = null, RenderContext? context = null) =>
        FilterTextMessagePresentation(html, requestHost, context).ToHtml();

    /// <summary>
    /// <see cref="ApplyTextMessagePresentationFilters"/> as the content it returns: SanitizeAttributes
    /// returns a string, which <c>ActionText::Content::Filter.apply</c> parses again
    /// (<c>Content.new(filter.apply, canonicalize: false)</c>, <c>reference/lib/rails_ext/filter.rb</c>).
    /// </summary>
    public static HtmlFragment FilterTextMessagePresentation(string html, string? requestHost = null, RenderContext? context = null)
    {
        // String#strip, not Unicode trim: NBSP and the other Unicode spaces stay.
        if (string.IsNullOrEmpty(html) || RubyString.Strip(html).Length == 0)
        {
            return RichTextRenderer.Wrap("");
        }

        var fragment = HtmlParser.ParseFragment(RubyString.Strip(html));
        CanonicalizeContent(fragment);
        ApplyRemoveSoloUnfurledLinkText(fragment, requestHost, context is null ? null : () => RichTextPlainText.ToPlainText(html, context));
        ApplySanitizeTags(fragment);
        return RichTextRenderer.Wrap(SafeListSanitizer.Sanitize(fragment.ToHtml(), SafeList.ContentFilter));
    }

    /// <summary>
    /// <c>ActionText::Content#fragment_by_canonicalizing_content</c>. Campfire's editor is Lexxy,
    /// whose <c>as_canonical</c> is the base editor's and leaves <c>figure[data-trix-attachment]</c>
    /// in place. Only the Trix editor converts that selector. Then attachments are minified,
    /// galleries canonicalized, and raw <c>action-text-markdown</c> tags unwrapped.
    /// </summary>
    public static void CanonicalizeContent(HtmlFragment fragment)
    {
        // Attachment.fragment_by_minifying_attachments: clear the inner HTML of each attachment.
        // 2. Clear inner HTML of all action-text-attachment nodes
        var attachments = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.Name == AttachmentResolution.TagName)
            .ToList();

        foreach (var att in attachments)
        {
            att.RemoveAllChildren();
        }

        // 3. Attachment gallery canonicalization:
        // div elements containing adjacent gallery attachments where all children are either
        // gallery attachments or whitespace text (newlines/spaces) are stripped of their attributes.
        static bool IsGalleryAttachment(HtmlNode node) =>
            node is HtmlElement e && e.Name == AttachmentResolution.TagName && e.GetAttribute("presentation") == "gallery";

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

        // MarkdownConversion.fragment_by_unwrapping_raw_markdown_tags: replace each
        // action-text-markdown element with its children, in document order.
        var markdown = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.Name == "action-text-markdown")
            .ToList();
        foreach (var node in markdown)
        {
            node.Parent?.ReplaceChild(node, node.Children);
        }
    }

    /// <summary>
    /// <c>ContentFilters::RemoveSoloUnfurledLinkText</c>. <c>applicable?</c> normalizes both sides
    /// of its <c>==</c> whatever the body holds, so the plain text is always computed and parsed
    /// as a URI when it names a Twitter domain, and either can raise.
    /// </summary>
    /// <param name="fragment">The canonicalized content.</param>
    /// <param name="requestHost">The request's host, which an embed's relative URL resolves against.</param>
    /// <param name="contentPlainText">
    /// <c>content.to_plain_text</c> with the app's attachables; without it, an approximation from
    /// the markup alone, for callers that only compare sanitized markup.
    /// </param>
    public static void ApplyRemoveSoloUnfurledLinkText(HtmlFragment fragment, string? requestHost, Func<string>? contentPlainText = null)
    {
        var unfurledLinks = fragment.Descendants().OfType<HtmlElement>()
            .Where(e => e.Name == AttachmentResolution.TagName &&
                        e.GetAttribute("content-type") == "application/vnd.actiontext.opengraph-embed")
            .ToList();

        var soloUrl = unfurledLinks.Count == 1 ? GetSoloUnfurledUrl(unfurledLinks[0], requestHost) : null;
        var normSolo = NormalizeTweetUrl(soloUrl);
        var normPlain = NormalizeTweetUrl(contentPlainText is null ? ToPlainText(fragment) : contentPlainText());

        if (normSolo is null || !string.Equals(normSolo, normPlain, StringComparison.Ordinal))
        {
            return;
        }

        var unfurl = unfurledLinks[0];
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
                var hasAttachment = p.Descendants().OfType<HtmlElement>().Any(e => e.Name == AttachmentResolution.TagName);
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

    // OpengraphEmbed.from_node(node)&.href: the node matched the content type already
    static string? GetSoloUnfurledUrl(HtmlElement node, string? requestHost) =>
        AttachmentResolution.AttributesFromNode(node, requestHost ?? "").Href;

    /// <summary><c>normalize_tweet_url</c>: nil stays nil.</summary>
    public static string? NormalizeTweetUrl(string? url)
    {
        // twitter_url?: url.present? && TWITTER_DOMAINS.any? { |domain| url.strip.include?(domain) }
        if (url is null || RubyText.IsBlank(url) || !TwitterDomains.Any(d => RubyString.Strip(url).Contains(d, StringComparison.Ordinal)))
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
        catch (RubyUriInvalidComponentException)
        {
            // normalize_tweet_url rescues URI::InvalidURIError only. InvalidComponentError
            // (a mailto whose opaque part URI rejects) propagates out of the filter.
            throw;
        }
        catch (RubyUriException)
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
            .Where(e => e.Name == AttachmentResolution.TagName)
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
