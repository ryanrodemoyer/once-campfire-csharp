using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/users/sidebars/show.html.erb and users/sidebars/rooms/_*.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>users/sidebars/show</c>: the sidebar with direct and shared rooms, unread states,
    /// direct room placeholders and navigation buttons.
    /// </summary>
    [ErbTemplate("users/sidebars/show.html.erb.cs")]
    public partial void UsersSidebarsShow(HtmlWriter w, SidebarPage page);

    /// <summary>
    /// <c>users/sidebars/rooms/_direct</c>: a direct room's link with avatar group or single avatar.
    /// </summary>
    [ErbTemplate("users/sidebars/rooms/_direct.html.erb.cs")]
    public partial void UsersSidebarsRoomsDirect(HtmlWriter w, SidebarDirectRoom direct);

    /// <summary>
    /// <c>users/sidebars/rooms/_direct_placeholder</c>: button to start a direct ping with a user.
    /// </summary>
    [ErbTemplate("users/sidebars/rooms/_direct_placeholder.html.erb.cs")]
    public partial void UsersSidebarsRoomsDirectPlaceholder(HtmlWriter w, SidebarPlaceholderUser user);

    /// <summary>
    /// <c>users/sidebars/rooms/_shared</c>: a shared room's link in the sidebar with unread indicator.
    /// </summary>
    [ErbTemplate("users/sidebars/rooms/_shared.html.erb.cs")]
    public partial void UsersSidebarsRoomsShared(HtmlWriter w, Room room, bool unread = false);

    /// <summary><c>sidebar_turbo_frame_tag do ... end</c> without a src.</summary>
    public static IHtml SidebarTurboFrameTag(Action body) => SidebarTurboFrameTag(null, body);
}
#pragma warning restore IDE0060

/// <summary>What <c>users/sidebars/show</c> and its partials read.</summary>
public sealed record SidebarPage(
    CurrentUser CurrentUser,
    IReadOnlyList<SidebarDirectRoom> DirectMemberships,
    IReadOnlyList<(Room Room, bool Unread)> OtherMemberships,
    IReadOnlyList<SidebarPlaceholderUser> DirectPlaceholderUsers,
    bool CanCreateRoom,
    string UserAvatarPath);

/// <summary>A user shown as a direct ping placeholder in the sidebar.</summary>
public sealed record SidebarPlaceholderUser(long Id, string Name, string AvatarPath);
