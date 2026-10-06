using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Attachments;
using Campfire.RichText.Html;
using Campfire.RichText.PlainText;

namespace Campfire.RichText.Editing;

/// <summary>
/// The edit round trip: what the <c>&lt;lexxy-editor&gt;</c> on <c>messages/edit.html.erb</c> is
/// given, and how the body it posts back is stored.
/// </summary>
public static class EditableContent
{
    /// <summary>
    /// The editor's <c>value</c>: <c>RichTextHelper#editable_body</c> (<c>reference/app/helpers/rich_text_helper.rb</c>)
    /// through Lexxy's <c>render_custom_attachments_in</c> (lexxy 0.9.24, <c>lib/lexxy/rich_text_area_tag.rb</c>).
    /// Null when the body is blank, so the editor gets no value. The caller escapes it into the attribute.
    /// </summary>
    /// <exception cref="RichTextRaisedException">
    /// Rails raises building it, as it does for any attachment that isn't a mention or an
    /// opengraph embed (<c>attachable_content_type</c> is undefined).
    /// </exception>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string? EditorValue(string? storedBody, RenderContext context)
    {
        var editable = EditableBody(storedBody, context);
        if (RubyText.IsBlank(editable))
        {
            return null;
        }

        // Every attachment without a URL gets its partial again, as a JSON string the editor decodes
        var fragment = RichTextRenderer.Wrap(editable);
        foreach (var node in RichTextPlainText.AttachmentNodes(fragment))
        {
            if (!RubyText.IsBlank(node.GetAttribute("url")))
            {
                continue;
            }
            var attachment = AttachmentResolution.AttachmentFromNode(node, context);
            node.SetAttribute("content", RailsJson.Encode(JsonValue.Create(RichTextRenderer.RenderAttachment(attachment, context))));
            if (node.GetAttribute("content-type") is null)
            {
                node.SetAttribute("content-type", ContentType(attachment.Attachable));
            }
        }
        return fragment.ToHtml();
    }

    /// <summary>
    /// <c>editable_body(message).body_before_type_cast</c>: the stored markup as is, with every
    /// attachment's content type and content rebuilt from its attachable, so Trix-era mentions and
    /// embeds, and hand-written embeds, reach the editor as Lexxy's own.
    /// </summary>
    /// <exception cref="RichTextRaisedException">Rails raises building it.</exception>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string EditableBody(string? storedBody, RenderContext context)
    {
        // ActionText::Fragment.wrap(message.body.body_before_type_cast)
        var fragment = RichTextRenderer.Wrap(storedBody ?? "");
        foreach (var node in RichTextPlainText.AttachmentNodes(fragment))
        {
            var attachment = AttachmentResolution.AttachmentFromNode(node, context);
            node.SetAttribute("content-type", attachment.Attachable.AttachableContentType);
            node.SetAttribute("content", RichTextRenderer.RenderAttachment(attachment, context));
        }
        return fragment.ToHtml();
    }

    /// <summary>
    /// <c>ActionText::Content.dump</c>: how a posted body is stored, canonicalized
    /// (<c>ActionText::Content.new(body).to_html</c>).
    /// </summary>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string StoredBody(string postedBody) => RichTextRenderer.Load(postedBody).ToHtml();

    /// <summary>
    /// <c>attachment.content_type</c>, delegated to the attachable, for an attachment that reached
    /// Lexxy without one. Only attachables that define <c>content_type</c> answer.
    /// </summary>
    static string ContentType(Attachable attachable) => attachable switch
    {
        RemoteVideo video => video.ContentType,
        _ => throw new RichTextRaisedException($"NoMethodError: undefined method 'content_type' for {attachable.GetType().Name}"),
    };
}
