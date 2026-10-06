using System.Text;

namespace Campfire.Web.Helpers;

// reference/app/helpers/users_helper.rb and users/avatars_helper.rb, filter_helper.rb,
// profiles_helper.rb, sidebar_helper.rb
public partial class View
{
    static readonly string[] AvatarColors =
    [
        "#AF2E1B", "#CC6324", "#3B4B59", "#BFA07A", "#ED8008", "#ED3F1C", "#BF1B1B", "#736B1E", "#D07B53",
        "#736356", "#AD1D1D", "#BF7C2A", "#C09C6F", "#698F9C", "#7C956B", "#5D618F", "#3B3633", "#67695E",
    ];

    /// <summary><c>avatar_background_color(user)</c>: picked by the CRC-32 of <c>user.to_param</c>.</summary>
    public static string AvatarBackgroundColor(string userToParam) =>
        AvatarColors[Crc32(Encoding.UTF8.GetBytes(userToParam)) % (uint)AvatarColors.Length];

    /// <summary><c>avatar_tag(user, **options)</c>: the avatar image linking to the user's page.</summary>
    public SafeString AvatarTag(AvatarUser user, HtmlOptions? options = null)
    {
        var imageOptions = new HtmlOptions { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 48 } }.Merge(options);
        return LinkTo(
            ImageTag(Routes.FreshUserAvatarPath(user.AvatarToken, user.UpdatedAt), imageOptions),
            Routes.UserPath(user.Id),
            new()
            {
                { "title", user.Title },
                { "class", "btn avatar" },
                { "data", new HtmlOptions { { "turbo_frame", "_top" } } },
            });
    }

    /// <summary><c>button_to_direct_room_with(user)</c>.</summary>
    public SafeString ButtonToDirectRoomWith(object userId) =>
        ButtonTo(
            ImageTag("messages.svg"),
            Routes.RoomsDirectsPath(new() { { "user_ids", new[] { userId } } }),
            new() { { "class", "btn btn--primary full-width txt--large" } });

    /// <summary><c>user_filter_menu_tag do ... end</c>.</summary>
    public static IHtml UserFilterMenuTag(Action body) =>
        Tag.Menu(new()
        {
            { "class", "flex flex-column gap margin-none pad overflow-y constrain-height" },
            { "data", new HtmlOptions { { "controller", "filter" }, { "filter_active_class", "filter--active" }, { "filter_selected_class", "selected" } } },
        }, body);

    /// <summary><c>user_filter_search_tag</c>.</summary>
    public static SafeString UserFilterSearchTag() =>
        Tag.Input(new()
        {
            { "type", "search" },
            { "id", "search" },
            { "autocorrect", "off" },
            { "autocomplete", "off" },
            { "data-1p-ignore", "true" },
            { "class", "input input--transparent full-width" },
            { "placeholder", "Filter…" },
            { "data", new HtmlOptions { { "action", "input->filter#filter" } } },
        });

    /// <summary>
    /// <c>profile_form_with(model, **params) do |form| ... end</c>: patches the signed-in user's
    /// profile. <paramref name="user"/> is <c>@user</c>.
    /// </summary>
    public IHtml ProfileFormWith(FormModel user, HtmlOptions? parameters, Action<FormBuilder> body)
    {
        var options = new HtmlOptions { { "method", "patch" }, { "data", new HtmlOptions { { "controller", "form" } } } }.Merge(parameters);
        return FormWith(user, Routes.UserProfilePath(), options, body);
    }

    /// <summary><c>profile_form_submit_button</c>.</summary>
    public SafeString ProfileFormSubmitButton() => SubmitButton("Save changes", "btn btn--reversed center txt-large");

    /// <summary><c>web_share_session_button(url, title, text) do ... end</c>.</summary>
    public static IHtml WebShareSessionButton(string url, string title, string text, Action body) =>
        Tag.Button(new()
        {
            { "class", "btn" },
            { "hidden", true },
            {
                "data", new HtmlOptions
                {
                    { "controller", "web-share" },
                    { "action", "web-share#share" },
                    { "web_share_url_value", url },
                    { "web_share_text_value", text },
                    { "web_share_title_value", title },
                }
            },
        }, body);

    /// <summary><c>sidebar_turbo_frame_tag(src:)</c> without a block.</summary>
    public static SafeString SidebarTurboFrameTag(string? src = null) => TurboFrameTag("user_sidebar", SidebarFrameAttributes(), src, "_top");

    /// <summary><c>sidebar_turbo_frame_tag(src:) do ... end</c>.</summary>
    public static IHtml SidebarTurboFrameTag(string? src, Action body) => TurboFrameTag("user_sidebar", SidebarFrameAttributes(), body, src, "_top");

    // Zlib.crc32.
    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    // A check-mark submit button with a screen-reader label.
    SafeString SubmitButton(string label, string classes) =>
        Tag.Button(
            OutputSafety.Concat(
                ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span(label, new() { { "class", "for-screen-reader" } })),
            new() { { "class", classes }, { "type", "submit" } });

    static HtmlOptions SidebarFrameAttributes() => new()
    {
        {
            "data", new HtmlOptions
            {
                { "turbo_permanent", true },
                { "controller", "rooms-list read-rooms turbo-frame" },
                { "rooms_list_unread_class", "unread" },
                // html_safe in the reference, so the arrows aren't escaped.
                { "action", new SafeString("presence:present@window->rooms-list#read read-rooms:read->rooms-list#read turbo:frame-load->rooms-list#loaded refresh-room:visible@window->turbo-frame#reload") },
            }
        },
    };
}

/// <summary>What <c>avatar_tag</c> reads from a user.</summary>
/// <param name="Id">The user's id (<c>to_param</c>).</param>
/// <param name="Title">The user's <c>title</c> (reference/app/models/user.rb).</param>
/// <param name="AvatarToken">The user's <c>avatar_token</c> (a signed id).</param>
/// <param name="UpdatedAt">The user's <c>updated_at</c>.</param>
public sealed record AvatarUser(object Id, string Title, string AvatarToken, DateTimeOffset UpdatedAt);
