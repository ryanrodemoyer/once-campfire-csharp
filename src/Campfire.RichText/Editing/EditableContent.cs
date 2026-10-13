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
    /// as <c>rich_textarea_tag</c> reads it, <c>to_editor_html</c> through the editor adapter
    /// Lexxy 0.9.24 registers on Rails main (<see cref="RichTextRenderer.ToEditorHtml"/>). Empty
    /// for a blank body, which the editor still gets as its value. The caller escapes it into
    /// the attribute.
    /// </summary>
    /// <exception cref="RichTextRaisedException">
    /// Rails raises building it, as it does for any attachment that isn't a mention or an
    /// opengraph embed (<c>attachable_content_type</c> is undefined).
    /// </exception>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string EditorValue(string? storedBody, RenderContext context) =>
        // `ActionText::RichText.new(body:)` casts the editable body through the serialized
        // attribute's coder, `Content.load(Content.dump(body))`: one more parse and serialization
        // than loading it, which shows where they aren't idempotent (each adds a `</plaintext>`)
        RichTextRenderer.ToEditorHtml(RichTextRenderer.Load(StoredBody(EditableBody(storedBody, context))), context);

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
}
