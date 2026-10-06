using Campfire.RichText.Attachments;
using Campfire.RichText.Html;
using Campfire.RichText.Sanitize;

namespace Campfire.RichText.PlainText;

/// <summary>
/// A message body as plain text: <c>Message#plain_text_body</c> (<c>reference/app/models/message.rb</c>),
/// which the search index (<c>reference/app/models/message/searchable.rb</c>), push notifications,
/// the message JSON, <c>/play</c> commands and emoji detection read, and the webhook's plain body
/// (<c>reference/app/models/webhook.rb</c>).
/// </summary>
public static class RichTextPlainText
{
    /// <summary>
    /// <c>message.plain_text_body</c>: <c>body.to_plain_text.presence || attachment&amp;.filename&amp;.to_s || ""</c>.
    /// </summary>
    /// <param name="body">The stored <c>action_text_rich_texts.body</c>, or null when there's no row or it's NULL.</param>
    /// <param name="attachmentFilename">The attached blob's filename, if the message has one.</param>
    /// <param name="context">The app's records and the request host.</param>
    /// <exception cref="RichTextRaisedException">Rails raises converting this body.</exception>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string PlainTextBody(string? body, string? attachmentFilename, RenderContext context) =>
        RubyText.Presence(ToPlainText(body, context)) ?? attachmentFilename ?? "";

    /// <summary>
    /// <c>RichText#to_plain_text</c> (<c>body&amp;.to_plain_text.to_s</c>) on the stored body, loaded
    /// as <c>ActionText::Content</c>: <c>render_attachments(with_full_attributes: false, &amp;:to_plain_text)</c>
    /// replaces each attachment with its plain text representation, parsed back as markup in its
    /// parent, and then the whole fragment converts.
    /// </summary>
    /// <exception cref="RichTextRaisedException">Rails raises converting this body.</exception>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string ToPlainText(string? body, RenderContext context)
    {
        if (body is null)
        {
            return "";
        }

        var content = RichTextRenderer.Load(body);
        foreach (var node in AttachmentNodes(content))
        {
            SanitizeContentAttribute(node);
            var attachment = AttachmentResolution.AttachmentFromNode(node, context);
            ReplaceWithHtml(node, PlainTextRepresentation(attachment));
        }
        return PlainTextConversion.NodeToPlainText(content);
    }

    /// <summary>
    /// <c>fragment.find_all("action-text-attachment")</c>, in document order. Nokogiri's CSS
    /// matches an HTML5 document's elements by local name, so this finds attachments inside SVG and
    /// MathML too.
    /// </summary>
    public static List<HtmlElement> AttachmentNodes(HtmlParentNode root) =>
        root.Descendants().OfType<HtmlElement>().Where(e => e.Name == AttachmentResolution.TagName).ToList();

    /// <summary>
    /// <c>Attachment#to_plain_text</c>: the attachable's <c>attachable_plain_text_representation(caption)</c>,
    /// or the caption when it has none.
    /// </summary>
    public static string PlainTextRepresentation(Attachment attachment) => attachment.Attachable switch
    {
        // User::Mentionable
        Mention mention => MentionPlainText(mention.User.Name),
        // ActionText::Attachment::OpengraphEmbed
        OpengraphEmbed => "",
        // ContentAttachment: content_instance.fragment.source, which replace serializes
        ContentAttachment content => RichTextRenderer.Load(content.Content).ToHtml(),
        RemoteImage => $"[{attachment.Caption ?? "Image"}]",
        // Lexxy's RemoteVideo
        RemoteVideo video => $"[{attachment.Caption ?? video.Filename ?? "Video"}]",
        MissingAttachable => attachment.Caption ?? "",
        _ => throw new InvalidOperationException($"Unknown attachable {attachment.Attachable}"),
    };

    /// <summary><c>User#attachable_plain_text_representation</c> (<c>reference/app/models/user/mentionable.rb</c>).</summary>
    public static string MentionPlainText(string userName) => "@" + userName;

    /// <summary>
    /// <c>Webhook#without_recipient_mentions</c>: the plain body with every <c>"@#{name}"</c> of the
    /// bot that receives it removed, then leading and trailing Unicode whitespace (<c>\p{Space}</c>).
    /// </summary>
    public static string WithoutRecipientMentions(string plainTextBody, string recipientName) =>
        plainTextBody.Replace(MentionPlainText(recipientName), "", StringComparison.Ordinal).Trim();

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
                node.SetAttribute("content", sanitized);
            }
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
