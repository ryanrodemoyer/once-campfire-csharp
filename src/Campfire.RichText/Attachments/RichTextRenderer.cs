using Campfire.RailsCompat.Ruby;
using Campfire.RichText.Html;
using Campfire.RichText.Sanitize;

namespace Campfire.RichText.Attachments;

/// <summary>
/// <c>ActionText::Content</c> rendering: <c>render_action_text_content</c> (attachments, then
/// galleries, then Action Text's sanitizer) and <c>Content#to_s</c>'s layout.
/// </summary>
/// <remarks>
/// Every step that serializes a node and parses the markup back (<c>Node#replace</c> with a
/// string, <c>inner_html=</c>) does the same here, in the same parse context, because those round
/// trips shape the output.
/// </remarks>
public static class RichTextRenderer
{
    const string galleryPresentation = "gallery";

    /// <summary><c>ActionText::Fragment.wrap(html)</c>: <c>ActionText::Content.new(html, canonicalize: false)</c>.</summary>
    public static HtmlFragment Wrap(string html) => HtmlParser.ParseFragment(RubyString.Strip(html));

    /// <summary><c>ActionText::Content.new(html)</c>, canonicalized as a stored body loads.</summary>
    public static HtmlFragment Load(string html)
    {
        var fragment = Wrap(html);
        ContentFilters.CanonicalizeContent(fragment);
        return fragment;
    }

    /// <summary>
    /// <c>Content#to_s</c>: the content partial inside <c>layouts/action_text/contents/_content.html.erb</c>,
    /// which Campfire overrides with a <c>lexxy-content</c> wrapper (<c>reference/app/views/layouts/action_text</c>).
    /// </summary>
    public static string RenderWithLayout(HtmlFragment content, RenderContext context) =>
        $"<div class=\"lexxy-content\">\n  {RenderContent(content, context)}\n</div>\n";

    /// <summary><c>render_action_text_content(content)</c></summary>
    public static string RenderContent(HtmlFragment content, RenderContext context)
    {
        var rendered = RenderAttachments(content, context);
        rendered = RenderAttachmentGalleries(rendered, context);
        return SafeListSanitizer.Sanitize(rendered.ToHtml(), SafeList.ActionText);
    }

    /// <summary>
    /// <c>render_action_text_attachment</c>, with a content attachment's content rendered by
    /// <c>ContentAttachment#to_html</c> (the content partial, without the layout).
    /// </summary>
    public static string RenderAttachment(Attachment attachment, RenderContext context) =>
        AttachmentPartials.Render(attachment, content => RenderContent(Load(content), context) + "\n", context);

    /// <summary>
    /// <c>Content#render_attachments</c> with <c>render_action_text_attachments</c>' block: each
    /// attachment's content attribute sanitized, then the node rebuilt with its full attributes
    /// around its rendered partial.
    /// </summary>
    static HtmlFragment RenderAttachments(HtmlFragment content, RenderContext context)
    {
        var source = (HtmlFragment)content.Clone();
        foreach (var node in AttachmentResolution.AttachmentNodes(source))
        {
            SanitizeContentAttribute(node);
            var attachment = AttachmentResolution.AttachmentFromNode(node, context);
            var full = NodeWithFullAttributes(node, attachment.Attachable);
            SetInnerHtml(full, RenderAttachment(attachment, context));
            ReplaceWithHtml(node, full.ToHtml());
        }
        return source;
    }

    /// <summary><c>Content#render_attachment_galleries</c> with the gallery layout.</summary>
    static HtmlFragment RenderAttachmentGalleries(HtmlFragment content, RenderContext context)
    {
        var source = (HtmlFragment)content.Clone();
        foreach (var gallery in AttachmentGalleryNodes(source))
        {
            var members = gallery.Descendants().OfType<HtmlElement>().Where(IsGalleryAttachment).ToList();
            var rendered = new System.Text.StringBuilder();
            foreach (var member in members)
            {
                var attachment = AttachmentResolution.AttachmentFromNode(member, context);
                var full = NodeWithFullAttributes(member, attachment.Attachable);
                SetInnerHtml(full, RenderAttachment(attachment, context));
                rendered.Append(full.ToHtml());
            }
            // action_text/attachment_galleries/_attachment_gallery.html.erb, chomped
            ReplaceWithHtml(gallery, $"<div class=\"attachment-gallery attachment-gallery--{members.Count}\">\n  {rendered}\n</div>");
        }
        return source;
    }

    /// <summary>
    /// <c>AttachmentGallery.find_attachment_gallery_nodes</c>: <c>div:has(A + A)</c> for gallery
    /// attachments A, whose children are all gallery attachments or newline and space text.
    /// </summary>
    public static List<HtmlElement> AttachmentGalleryNodes(HtmlParentNode root) =>
        root.Descendants().OfType<HtmlElement>()
            .Where(div => div.IsHtml("div"))
            .Where(div => div.Descendants().OfType<HtmlElement>().Any(FollowsGalleryAttachment))
            .Where(div => div.Children.All(child => child switch
            {
                HtmlText text => text.Data.All(c => c is '\n' or ' '),
                HtmlElement element => IsGalleryAttachment(element),
                _ => false,
            }))
            .ToList();

    static bool IsGalleryAttachment(HtmlElement element) =>
        element.IsHtml(AttachmentResolution.TagName) && element.GetAttribute("presentation") == galleryPresentation;

    // A + A: a gallery attachment whose previous element sibling is one too
    static bool FollowsGalleryAttachment(HtmlElement element)
    {
        if (!IsGalleryAttachment(element) || element.Parent is not { } parent)
        {
            return false;
        }
        var siblings = parent.Elements.ToList();
        var index = siblings.IndexOf(element);
        return index > 0 && IsGalleryAttachment(siblings[index - 1]);
    }

    /// <summary>
    /// <c>render_attachments</c>' first step: the <c>content</c> attribute is sanitized with Action
    /// Text's allowlist, and dropped if that leaves nothing.
    /// </summary>
    static void SanitizeContentAttribute(HtmlElement node)
    {
        if (node.RemoveAttribute("content") is { } content)
        {
            var sanitized = SafeListSanitizer.Sanitize(content, SafeList.ActionText);
            if (!RubyText.IsBlank(sanitized))
            {
                NokogiriAttribute.Set(node, "content", sanitized);
            }
        }
    }

    /// <summary>
    /// <c>Attachment#with_full_attributes</c>: a new node carrying the node's attachment attributes,
    /// the attachable's own (a user's sgid and content type), and the node's sgid if it had one.
    /// </summary>
    static HtmlElement NodeWithFullAttributes(HtmlElement node, Attachable attachable)
    {
        var element = new HtmlElement(AttachmentResolution.TagName);
        foreach (var name in SafeList.AttachmentAttributes)
        {
            var value = (name, attachable) switch
            {
                ("sgid", Mention mention) => node.GetAttribute("sgid") ?? mention.User.AttachableSgid,
                ("content-type", Mention) => Attachable.MentionContentType,
                _ => node.GetAttribute(name),
            };
            if (value is not null)
            {
                NokogiriAttribute.Set(element, name, value);
            }
        }
        if (element.Attributes.Count == 0)
        {
            // from_attributes returns nil, and the render block calls #node on it
            throw new RichTextRaisedException("NoMethodError: undefined method 'node' for nil");
        }
        return element;
    }

    /// <summary><c>Node#inner_html=</c>: the markup parsed in the node's own context.</summary>
    static void SetInnerHtml(HtmlElement node, string html)
    {
        var parsed = HtmlParser.ParseFragment(html, FragmentContext.For(node));
        node.RemoveAllChildren();
        foreach (var child in parsed.Children.ToList())
        {
            node.AppendChild(child);
        }
    }

    /// <summary><c>Node#replace</c> with a string: the markup parsed in the parent's context.</summary>
    static void ReplaceWithHtml(HtmlNode node, string html)
    {
        var parent = node.Parent ?? throw new RichTextRaisedException("RuntimeError: Cannot replace a node with no parent");
        var parsed = HtmlParser.ParseFragment(html, FragmentContext.For(parent));
        parent.ReplaceChild(node, parsed.Children.ToList());
    }
}
