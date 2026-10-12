using Campfire.RichText.Editing;

namespace Campfire.Web.Helpers;

// reference/app/views/messages/{create,destroy,edit,show,room_not_found}, and the helpers only the
// edit page uses.
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>messages/create.turbo_stream</c>: the new message appended to its room's messages.</summary>
    [ErbTemplate("messages/create.turbo_stream.erb.cs")]
    public partial void MessagesCreate(HtmlWriter w, MessageView message, RecordKey room);

    /// <summary><c>messages/destroy.turbo_stream</c>: the message removed.</summary>
    [ErbTemplate("messages/destroy.turbo_stream.erb.cs")]
    public partial void MessagesDestroy(HtmlWriter w, MessageView message);

    /// <summary><c>messages/show</c>: <c>render @message</c>.</summary>
    [ErbTemplate("messages/show.html.erb.cs")]
    public partial void MessagesShow(HtmlWriter w, MessageView message);

    /// <summary><c>messages/edit</c>: the editor, or for an attachment, the attachment and a delete button.</summary>
    [ErbTemplate("messages/edit.html.erb.cs")]
    public partial void MessagesEdit(HtmlWriter w, MessageView message);

    /// <summary><c>messages/room_not_found</c>: the composer frame saying the room is gone.</summary>
    [ErbTemplate("messages/room_not_found.html.erb.cs")]
    public partial void MessagesRoomNotFound(HtmlWriter w);

    /// <summary><c>message_attachment_presentation(message)</c>, without <c>message_presentation</c>'s rescue.</summary>
    internal SafeString MessageAttachmentPresentation(MessageView message) => new AttachmentPresentation(this, message.Attachment!, message.IsThumbProcessed, message.IsPosterProcessed).Render();

    /// <summary>
    /// <c>editable_body(@message)</c> as <c>rich_textarea_tag</c> passes it on: its
    /// <c>to_editor_html</c>, empty when the body is blank.
    /// </summary>
    internal string EditorValue(MessageView message) =>
        EditableContent.EditorValue(message.Body, RichTextContext ?? throw new InvalidOperationException("View.RichTextContext isn't set"));

    /// <summary>
    /// <c>form.rich_text_area method, options do ... end</c> through Rails main's editor adapter,
    /// which Lexxy 0.9.24 registers (lib/lexxy/engine.rb): <c>Tags::ActionText#render</c> names and
    /// identifies the field, <c>rich_textarea_tag</c> (actiontext/app/helpers/action_text/tag_helper.rb)
    /// then sets the <c>value</c>, the <c>name</c> and Active Storage's upload URLs, and
    /// <c>Editor::Tag#render_in</c> (actiontext/lib/action_text/editor.rb) the default class. No
    /// <c>input</c>: that was Lexxy's fallback for Rails without adapters.
    /// <paramref name="options"/> carries the <c>value</c>.
    /// </summary>
    internal IHtml LexxyRichTextArea(FormBuilder form, string method, HtmlOptions options, Action body)
    {
        var attributes = options.Clone();
        var value = attributes.Delete("value");
        attributes["id"] = form.FieldId(method);
        attributes["value"] = value;
        attributes["name"] = form.FieldName(method);
        var data = attributes["data"] as HtmlOptions ?? [];
        data["direct_upload_url"] ??= Routes.RailsDirectUploadsUrl(Origin);
        data["blob_url_template"] ??= Routes.RailsServiceBlobUrl(Origin, ":signed_id", ":filename");
        attributes["data"] = data;
        attributes["class"] ??= "lexxy-content";
        return TagHelper.ContentTag("lexxy-editor", attributes, body);
    }
}
#pragma warning restore IDE0060
