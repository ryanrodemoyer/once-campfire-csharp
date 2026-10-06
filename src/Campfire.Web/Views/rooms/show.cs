using Campfire.Data.Records;
using Campfire.RailsCompat.UserAgent;

namespace Campfire.Web.Helpers;

// reference/app/views/rooms/show.html.erb and rooms/show/_*.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>rooms/show</c>: a room's latest messages (or those around a deep link) with its nav and composer.</summary>
    [ErbTemplate("rooms/show.html.erb.cs")]
    public partial void RoomsShow(HtmlWriter w, RoomPage page);

    /// <summary><c>rooms/show/_nav</c>: the room's name, settings link and notification bell.</summary>
    [ErbTemplate("rooms/show/_nav.html.erb.cs")]
    public partial void RoomsShowNav(HtmlWriter w, RoomPage page);

    /// <summary><c>rooms/show/_composer</c>: the message form in the footer.</summary>
    [ErbTemplate("rooms/show/_composer.html.erb.cs")]
    public partial void RoomsShowComposer(HtmlWriter w, RoomPage page);

    /// <summary><c>rooms/show/_invitation</c>: the welcome and join link, in the first room until it fills a page.</summary>
    [ErbTemplate("rooms/show/_invitation.html.erb.cs")]
    public partial void RoomsShowInvitation(HtmlWriter w, RoomPage page);

    /// <summary>
    /// ActionView's <c>capture</c> of a block whose output is blank: it yields the block's value
    /// instead, which for an ERB block is the last text it appended (<paramref name="lastText"/>).
    /// So an empty room's <c>messages_tag</c> holds just a newline.
    /// </summary>
    internal static void CaptureOrLastText(HtmlWriter w, string lastText, Action body)
    {
        var output = w.Capture(body);
        w.AppendRaw(RubyValues.IsBlank(output.ToString()) ? lastText : output.ToString());
    }

    /// <summary>
    /// turbo-rails' <c>turbo_exempts_page_from_preview</c>: <c>provide :head</c> of the
    /// <c>turbo-cache-control</c> meta tag, so it outputs nothing where it is called.
    /// </summary>
    internal SafeString TurboExemptsPageFromPreview()
    {
        ContentFor("head", Tag.Meta(new() { { "name", "turbo-cache-control" }, { "content", "no-preview" } }));
        return SafeString.Empty;
    }

    /// <summary>
    /// <c>form.rich_text_area :body, options do ... end</c> for <c>Message.new</c>, as Lexxy 0.9
    /// renders it without editor adapters (lexxy/action_text_tag.rb, lexxy/rich_text_area_tag.rb):
    /// a <c>lexxy-editor</c> named and identified like any field, whose <c>input</c> is
    /// <c>dom_id(object, "&lt;id&gt;_trix_input")</c>, with Active Storage's upload URLs and no
    /// value for an empty body.
    /// </summary>
    internal IHtml ComposerRichTextArea(FormBuilder form, string method, HtmlOptions options, Action body)
    {
        var attributes = options.Clone();
        var id = form.FieldId(method);
        attributes["id"] = id;
        attributes["input"] = RecordIdentifier.DomId(RecordKey.New(Message.ModelName), $"{id}_trix_input");
        attributes["class"] ??= "lexxy-content";
        var data = attributes["data"] as HtmlOptions ?? [];
        data["direct_upload_url"] ??= Routes.RailsDirectUploadsUrl(Origin);
        data["blob_url_template"] ??= Routes.RailsServiceBlobUrl(Origin, ":signed_id", ":filename");
        attributes["data"] = data;
        attributes["name"] = form.FieldName(method);
        return TagHelper.ContentTag("lexxy-editor", attributes, body);
    }
}
#pragma warning restore IDE0060

/// <summary>What <c>rooms/show</c> and its partials read.</summary>
/// <param name="Room">The room (<c>@room</c>).</param>
/// <param name="DisplayName"><c>room_display_name(@room)</c> for the current user.</param>
/// <param name="Messages"><c>@messages</c>, loaded for <c>messages/_message</c>.</param>
/// <param name="CurrentUser"><c>Current.user</c> as <c>messages/_template</c> reads it.</param>
/// <param name="ShowsInvitation"><c>@room == Room.original &amp;&amp; !@room.messages.paged?</c>.</param>
/// <param name="JoinCode"><c>Current.account.join_code</c>.</param>
/// <param name="Platform">The request's <c>platform</c>, for the notification help.</param>
public sealed record RoomPage(
    Room Room,
    string? DisplayName,
    IReadOnlyList<MessageView> Messages,
    MessageUser CurrentUser,
    bool ShowsInvitation,
    string? JoinCode,
    ApplicationPlatform Platform)
{
    /// <summary>The room's record key: its STI class names its <c>dom_id</c>s and stream.</summary>
    public RecordKey Key => new(Room.Type.ClassName(), Room.Id);

    /// <summary><c>polymorphic_path([ :edit, @room ])</c>.</summary>
    public string EditPath => Room.Type switch
    {
        RoomType.Open => Routes.EditRoomsOpenPath(Room.Id),
        RoomType.Closed => Routes.EditRoomsClosedPath(Room.Id),
        _ => Routes.EditRoomsDirectPath(Room.Id),
    };
}
