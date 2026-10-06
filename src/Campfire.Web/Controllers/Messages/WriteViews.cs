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
    SafeString MessageAttachmentPresentation(MessageView message) => new AttachmentPresentation(this, message.Attachment!).Render();

    /// <summary>
    /// <c>editable_body(@message)</c> as Lexxy's <c>lexxy_rich_textarea_tag</c> passes it on:
    /// <c>render_custom_attachments_in</c> of it, or null when the body is blank.
    /// </summary>
    string? EditorValue(MessageView message) =>
        EditableContent.EditorValue(message.Body, RichTextContext ?? throw new InvalidOperationException("View.RichTextContext isn't set"));

    /// <summary>
    /// <c>form.rich_text_area method, options do ... end</c> as Lexxy 0.9 replaces it on Rails
    /// without editor adapters (lexxy/action_text_tag.rb, lexxy/rich_text_area_tag.rb): a
    /// <c>lexxy-editor</c> named and identified like any field, whose <c>input</c> is
    /// <c>dom_id(object, "&lt;id&gt;_trix_input")</c>, with Active Storage's upload URLs.
    /// <paramref name="options"/> carries the <c>value</c>.
    /// </summary>
    IHtml LexxyRichTextArea(FormBuilder form, string method, MessageView message, HtmlOptions options, Action body)
    {
        var attributes = options.Clone();
        var value = attributes.Delete("value");
        var name = form.FieldName(method);
        var id = form.FieldId(method);
        attributes["id"] = id;
        attributes["input"] = RecordIdentifier.DomId(message.Record, $"{id}_trix_input");
        attributes["class"] ??= "lexxy-content";
        var data = attributes["data"] as HtmlOptions ?? [];
        data["direct_upload_url"] ??= Routes.RailsDirectUploadsUrl(Origin);
        data["blob_url_template"] ??= Routes.RailsServiceBlobUrl(Origin, ":signed_id", ":filename");
        attributes["data"] = data;
        attributes["name"] = name;
        attributes["value"] = value;
        return TagHelper.ContentTag("lexxy-editor", attributes, body);
    }
}
#pragma warning restore IDE0060
