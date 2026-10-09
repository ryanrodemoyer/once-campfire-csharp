using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/rooms/layouts/_new.html.erb, _edit.html.erb and _form.html.erb, the
// layouts the open and closed room pages render their forms in, and stand-ins for the sidebar's
// room partials, which the room settings broadcast.
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>render layout: "rooms/layouts/new" do ... end</c>: the new room panel around
    /// <paramref name="content"/>, with a link back to the last room visited.
    /// </summary>
    [ErbTemplate("rooms/layouts/_new.html.erb.cs")]
    public partial void RoomsLayoutsNew(HtmlWriter w, object? lastRoomId, SafeString content, string? viewTransitionName = null);

    /// <summary>
    /// <c>render layout: "rooms/layouts/edit", locals: { room: } do ... end</c>: the room's
    /// settings panel around <paramref name="content"/>, and its delete button for those who can
    /// administer it.
    /// </summary>
    [ErbTemplate("rooms/layouts/_edit.html.erb.cs")]
    public partial void RoomsLayoutsEdit(HtmlWriter w, RoomForm room, SafeString content);

    /// <summary>
    /// <c>render layout: "rooms/layouts/form", locals: { room: } do ... end</c>: the room's name
    /// (a field for those who can administer it) above <paramref name="content"/>, its access
    /// list.
    /// </summary>
    [ErbTemplate("rooms/layouts/_form.html.erb.cs")]
    public partial void RoomsLayoutsForm(HtmlWriter w, RoomForm room, SafeString content);

    /// <summary>
    /// <c>users/sidebars/rooms/_shared</c> (M01's template, not yet ported): a shared room's
    /// sidebar link, which room creation, updates and involvement changes broadcast.
    /// </summary>
    [ErbTemplate("rooms/layouts/_sidebar_shared.html.erb.cs")]
    public partial void RoomsSidebarShared(HtmlWriter w, Room room);

    /// <summary>
    /// <c>users/sidebars/rooms/_direct</c> (M01's template, not yet ported): a direct room's
    /// sidebar link for one of its members, which creating the direct room broadcasts.
    /// </summary>
    [ErbTemplate("rooms/layouts/_sidebar_direct.html.erb.cs")]
    public partial void RoomsSidebarDirect(HtmlWriter w, SidebarDirectRoom direct);

    // The initials of a group direct room's members: `name.split(' ')[0, 3].map { |str|
    // str[0].capitalize }.join`, Ruby's whitespace split.
    internal static string MemberInitials(string name) =>
        string.Concat(name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(3)
            .Select(part => RubyCapitalize(char.IsSurrogate(part[0]) ? part[..2] : part[..1])));

    // `name.split(' ')[0]`
    internal static string? FirstName(string name) =>
        name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    static string RubyCapitalize(string character) => character.ToUpperInvariant();
}
#pragma warning restore IDE0060

/// <summary>
/// A room as its settings pages read it: an open or closed room (new, or persisted with
/// <see cref="Id"/>) as the controller's <c>@room</c> is typed, since <c>becomes!</c> lets an
/// open room be edited as a closed one and back.
/// </summary>
/// <param name="Type">The room's type on this page.</param>
/// <param name="Id">The room's id; null for a new room.</param>
/// <param name="Name">The room's name.</param>
/// <param name="CanAdminister"><c>Current.user.can_administer?(room)</c>.</param>
/// <param name="TypeChangePath">The page for the other type: the access switch links to it.</param>
/// <param name="LastRoomId"><c>last_room_visited</c>'s id, for the back link.</param>
/// <param name="DisplayName"><c>room_display_name(room)</c>, on the delete button.</param>
public sealed record RoomForm(RoomType Type, long? Id, string? Name, bool CanAdminister, string TypeChangePath, long? LastRoomId, string? DisplayName = null)
{
    public bool IsNewRecord => Id is null;

    public RecordKey Key => new(Type.ClassName(), Id);

    /// <summary><c>form_with model: room</c>'s model: its name is the field's value.</summary>
    public FormModel Model => new(Key, !IsNewRecord, new Dictionary<string, object?> { ["name"] = Name });

    /// <summary><c>polymorphic_path(room)</c>: the type's collection for a new room, else its member.</summary>
    public string Url => (Type, Id) switch
    {
        (RoomType.Open, null) => Routes.RoomsOpensPath(),
        (RoomType.Open, { } id) => Routes.RoomsOpenPath(id),
        (RoomType.Closed, null) => Routes.RoomsClosedsPath(),
        (RoomType.Closed, { } id) => Routes.RoomsClosedPath(id),
        _ => throw new InvalidOperationException("Direct rooms have no room form"),
    };
}

/// <summary>A user in a room's access list (<c>rooms/opens/_user</c>, <c>rooms/closeds/_user</c>).</summary>
/// <param name="Id">The user's id.</param>
/// <param name="Name">The user's name.</param>
/// <param name="Avatar">What <c>avatar_tag</c> reads.</param>
public sealed record RoomMember(long Id, string Name, AvatarUser Avatar);

/// <summary>
/// What <c>users/sidebars/rooms/_direct</c> reads for one membership of a direct room.
/// </summary>
/// <param name="RoomId">The room's id.</param>
/// <param name="RoomUpdatedAt">The room's <c>updated_at</c>, which sorts the list.</param>
/// <param name="Unread"><c>membership.unread?</c>.</param>
/// <param name="Members">
/// <c>membership.room.users.without(membership.user).presence || [ membership.user ]</c>: each
/// one's name and avatar path.
/// </param>
public sealed record SidebarDirectRoom(long RoomId, DateTimeOffset RoomUpdatedAt, bool Unread, IReadOnlyList<(string Name, string AvatarPath)> Members);
