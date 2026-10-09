using Campfire.Data.Records;
using Campfire.RailsCompat.UserAgent;

namespace Campfire.Web.Helpers;

// reference/app/views/users/profiles/{show,_membership}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>users/profiles/show</c>: the signed-in person's own profile, rooms and sign-in link.</summary>
    [ErbTemplate("users/profiles/show.html.erb.cs")]
    public partial void UsersProfilesShow(HtmlWriter w, ProfilePage page);

    /// <summary><c>users/profiles/_membership</c>: a room and the button that cycles its involvement.</summary>
    [ErbTemplate("users/profiles/_membership.html.erb.cs")]
    public partial void UsersProfilesMembership(HtmlWriter w, ProfileMembership membership);
}
#pragma warning restore IDE0060

/// <summary>What <c>users/profiles/show</c> reads.</summary>
/// <param name="User"><c>@user</c>, the signed-in person.</param>
/// <param name="Form">The profile forms' model: <c>@user</c>'s name, email address and bio.</param>
/// <param name="AvatarPath"><c>fresh_user_avatar_path(@user)</c>.</param>
/// <param name="AvatarAttached"><c>@user.avatar.attached?</c>.</param>
/// <param name="SharedMemberships"><c>@shared_memberships</c>, by room name.</param>
/// <param name="DirectMemberships"><c>@direct_memberships</c>, by room name.</param>
/// <param name="TransferUrl"><c>session_transfer_url(@user.transfer_id)</c>.</param>
/// <param name="Platform">The request's platform, for <c>pwa/_install_instructions</c>.</param>
public sealed record ProfilePage(
    User User,
    FormModel Form,
    string AvatarPath,
    bool AvatarAttached,
    IReadOnlyList<ProfileMembership> SharedMemberships,
    IReadOnlyList<ProfileMembership> DirectMemberships,
    string TransferUrl,
    ApplicationPlatform Platform);

/// <summary>A membership as <c>users/profiles/_membership</c> shows it.</summary>
/// <param name="Room">The room's record key, for its DOM ids.</param>
/// <param name="RoomId">The room's id.</param>
/// <param name="IsDirect">Whether the room is a direct one.</param>
/// <param name="DisplayName"><c>room_display_name(membership.room)</c> for the signed-in person.</param>
/// <param name="Involvement">The membership's involvement, by name.</param>
public sealed record ProfileMembership(RecordKey Room, long RoomId, bool IsDirect, string? DisplayName, string Involvement);
