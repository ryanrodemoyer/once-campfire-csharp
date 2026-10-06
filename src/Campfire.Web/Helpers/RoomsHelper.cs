using Campfire.RailsCompat.Formatting;

namespace Campfire.Web.Helpers;

// reference/app/helpers/rooms_helper.rb, rooms/involvements_helper.rb and the tag helpers of
// messages_helper.rb. Message presentation (rich text, attachments, sounds) belongs to M01.
public partial class View
{
    static readonly Dictionary<string, string> HumanizeInvolvement = new(StringComparer.Ordinal)
    {
        ["mentions"] = "Notifying about @ mentions",
        ["everything"] = "Notifying about all messages",
        ["nothing"] = "Notifications are off",
        ["invisible"] = "Notifications are off and room invisible in sidebar",
    };

    static readonly string[] SharedInvolvementOrder = ["mentions", "everything", "nothing", "invisible"];
    static readonly string[] DirectInvolvementOrder = ["everything", "nothing"];

    /// <summary><c>link_to_room(room, **attributes) do ... end</c>.</summary>
    public static IHtml LinkToRoom(object roomId, HtmlOptions? attributes, Action body) =>
        LinkTo(Routes.RoomPath(roomId), LinkToRoomOptions(roomId, attributes), body);

    /// <summary><c>link_to_edit_room(room) do ... end</c>, given the room's edit URL (<c>[:edit, @room]</c>).</summary>
    public static IHtml LinkToEditRoom(object roomId, string editRoomPath, Action body) =>
        LinkTo(editRoomPath, new()
        {
            { "class", "btn" },
            { "style", $"view-transition-name: edit-room-{RubyValues.ToS(roomId)}" },
            { "data", new HtmlOptions { { "room_id", roomId } } },
        }, body);

    /// <summary><c>link_back_to_last_room_visited</c>, given <c>last_room_visited</c>'s id.</summary>
    public SafeString LinkBackToLastRoomVisited(object? lastRoomId) =>
        LinkBackTo(lastRoomId is null ? Routes.RootPath() : Routes.RoomPath(lastRoomId));

    /// <summary>
    /// <c>button_to_delete_room(room, url:)</c>. <paramref name="displayName"/> is
    /// <c>room_display_name(room)</c>; <paramref name="url"/> defaults to <c>room_url(room)</c>.
    /// </summary>
    public SafeString ButtonToDeleteRoom(object roomId, string roomName, string displayName, string? url = null) =>
        ButtonTo(
            OutputSafety.Concat(
                ImageTag("trash.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span(displayName, new() { { "class", "overflow-ellipsis" } })),
            url ?? Routes.RoomUrl(Origin, roomId),
            new()
            {
                { "method", "delete" },
                { "class", "btn btn--negative max-width" },
                { "aria", new HtmlOptions { { "label", $"Delete {roomName}" } } },
                { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to delete this room and all messages in it? This can’t be undone." } } },
            });

    /// <summary><c>button_to_jump_to_newest_message</c>.</summary>
    public SafeString ButtonToJumpToNewestMessage() =>
        Tag.Button(
            OutputSafety.Concat(
                ImageTag("arrow-down.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span("Jump to newest message", new() { { "class", "for-screen-reader" } })),
            new()
            {
                { "class", "message-area__return-to-latest btn" },
                { "data", new HtmlOptions { { "action", "messages#returnToLatest" }, { "messages_target", "latest" } } },
                { "hidden", true },
            });

    /// <summary><c>submit_room_button_tag</c>.</summary>
    public SafeString SubmitRoomButtonTag() =>
        ButtonTag(
            OutputSafety.Concat(
                ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span("Save", new() { { "class", "for-screen-reader" } })),
            new() { { "class", "btn btn--reversed txt-large center" }, { "type", "submit" } });

    /// <summary><c>composer_form_tag(room) do |form| ... end</c>.</summary>
    public IHtml ComposerFormTag(object roomId, Action<FormBuilder> body) =>
        FormWith(FormModel.New("Message"), Routes.RoomMessagesPath(roomId), new()
        {
            { "id", "composer" },
            { "class", "margin-block flex-item-grow contain" },
            {
                "data", new HtmlOptions
                {
                    { "controller", "composer drop-target" },
                    { "action", ComposerDataActions() },
                    { "composer_messages_outlet", "#message-area" },
                    { "composer_toolbar_class", "composer--rich-text" },
                    { "composer_room_id_value", roomId },
                }
            },
        }, body);

    /// <summary><c>turbo_frame_for_involvement_tag(room) do ... end</c>.</summary>
    public static IHtml TurboFrameForInvolvementTag(RecordKey room, object roomId, Action body) =>
        TurboFrameTag(RecordIdentifier.DomId(room, "involvement"), new()
        {
            {
                "data", new HtmlOptions
                {
                    { "controller", "turbo-frame" },
                    { "action", "notifications:ready@window->turbo-frame#load" },
                    { "turbo_frame_url_param", Routes.RoomInvolvementPath(roomId) },
                }
            },
        }, body);

    /// <summary><c>button_to_change_involvement(room, involvement)</c>: cycles to the next involvement.</summary>
    public SafeString ButtonToChangeInvolvement(RecordKey room, object roomId, bool isDirect, string involvement) =>
        ButtonTo(
            OutputSafety.Concat(
                ImageTag($"notification-bell-{involvement}.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span(HumanizeInvolvement.GetValueOrDefault(involvement), new()
                {
                    { "class", "for-screen-reader" },
                    { "id", RecordIdentifier.DomId(room, "involvement_label") },
                })),
            Routes.RoomInvolvementPath(roomId, new() { { "involvement", NextInvolvement(isDirect, involvement) } }),
            new()
            {
                { "method", "put" },
                { "role", "checkbox" },
                { "aria", new HtmlOptions { { "checked", true }, { "labelledby", RecordIdentifier.DomId(room, "involvement_label") } } },
                { "tabindex", 0 },
                { "class", $"btn {involvement}" },
            });

    /// <summary><c>message_area_tag(room) do ... end</c>.</summary>
    public IHtml MessageAreaTag(object roomId, Action body) =>
        Tag.Div(new()
        {
            { "id", "message-area" },
            { "class", "message-area" },
            { "contents", true },
            {
                "data", new HtmlOptions
                {
                    { "controller", "messages presence drop-target" },
                    { "action", string.Join(" ", messagesActions, DropTargetActions(), presenceActions) },
                    { "messages_first_of_day_class", "message--first-of-day" },
                    { "messages_formatted_class", "message--formatted" },
                    { "messages_me_class", "message--me" },
                    { "messages_mentioned_class", "message--mentioned" },
                    { "messages_threaded_class", "message--threaded" },
                    { "messages_page_url_value", Routes.RoomMessagesUrl(Origin, roomId) },
                }
            },
        }, body);

    /// <summary><c>messages_tag(room) do ... end</c>.</summary>
    public IHtml MessagesTag(RecordKey room, object roomId, DateTimeOffset roomUpdatedAt, Action body) =>
        Tag.Div(new()
        {
            { "id", RecordIdentifier.DomId(room, "messages") },
            { "class", "messages" },
            {
                "data", new HtmlOptions
                {
                    { "controller", "maintain-scroll refresh-room" },
                    { "action", $"{maintainScrollActions} {refreshRoomActions}" },
                    { "messages_target", "messages" },
                    { "refresh_room_loaded_at_value", TimeFormats.ToFsEpoch(roomUpdatedAt) },
                    { "refresh_room_url_value", Routes.RoomRefreshUrl(Origin, roomId) },
                }
            },
        }, body);

    /// <summary><c>message_tag(message) do ... end</c>.</summary>
    public static IHtml MessageTag(MessageTagInfo message, Action body)
    {
        var timestamp = TimeFormats.ToFsEpoch(message.CreatedAt);
        return Tag.Div(new()
        {
            { "id", RecordIdentifier.DomId(RecordKey.New("Message") with { Key = message.ClientMessageId }) },
            { "class", $"message {(message.AllEmoji ? "message--emoji" : "")}" },
            {
                "data", new HtmlOptions
                {
                    { "controller", "reply" },
                    { "user_id", message.CreatorId },
                    { "message_id", message.Id },
                    { "message_timestamp", timestamp },
                    { "message_updated_at", TimeFormats.ToFsEpoch(message.UpdatedAt) },
                    { "sort_value", timestamp },
                    { "messages_target", "message" },
                    { "search_results_target", "message" },
                    { "refresh_room_target", "message" },
                    { "reply_composer_outlet", "#composer" },
                }
            },
        }, body);
    }

    /// <summary><c>message_timestamp(message, **attributes)</c>.</summary>
    public static SafeString MessageTimestamp(DateTimeOffset messageCreatedAt, HtmlOptions? attributes = null) =>
        LocalDatetimeTag(messageCreatedAt, attributes: attributes);

    /// <summary><c>message_author_title(author)</c>: name and bio, the blank ones left out.</summary>
    public static string MessageAuthorTitle(string? name, string? bio) =>
        string.Join(" – ", new[] { name, bio }.Where(RubyValues.IsPresent));

    const string messagesActions = "turbo:before-stream-render@document->messages#beforeStreamRender keydown.up@document->messages#editMyLastMessage";
    const string maintainScrollActions = "turbo:before-stream-render@document->maintain-scroll#beforeStreamRender";
    const string refreshRoomActions = "visibilitychange@document->refresh-room#visibilityChanged online@window->refresh-room#online";
    const string presenceActions = "visibilitychange@document->presence#visibilityChanged";

    static HtmlOptions LinkToRoomOptions(object roomId, HtmlOptions? attributes)
    {
        var options = (attributes ?? []).Clone();
        var data = new HtmlOptions
        {
            { "rooms_list_target", "room" },
            { "room_id", roomId },
            { "badge_dot_target", "unread" },
            { "sorted_list_target", "item" },
        }.Merge(options["data"] as HtmlOptions);
        options["data"] = data;
        return options;
    }

    static string ComposerDataActions() => string.Join(" ",
        DropTargetActions(),
        "drop-target:drop@window->composer#dropFiles",
        "lexxy:file-accept->composer#preventAttachment refresh-room:online@window->composer#online",
        "typing-notifications#stop paste->composer#pasteFiles turbo:submit-end->composer#submitEnd refresh-room:offline@window->composer#offline");

    static string NextInvolvement(bool isDirect, string involvement)
    {
        var order = isDirect ? DirectInvolvementOrder : SharedInvolvementOrder;
        var index = Array.IndexOf(order, involvement);
        return index >= 0 && index + 1 < order.Length ? order[index + 1] : order[0];
    }
}

/// <summary>
/// What <c>message_tag</c> reads from a message; <c>AllEmoji</c> is
/// <c>message.plain_text_body.all_emoji?</c>.
/// </summary>
public sealed record MessageTagInfo(object Id, object ClientMessageId, object CreatorId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool AllEmoji);
