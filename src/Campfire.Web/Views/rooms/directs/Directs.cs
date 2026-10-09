namespace Campfire.Web.Helpers;

// reference/app/views/rooms/directs/{new,edit}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>rooms/directs/new</c>: the sidebar's form for starting a Ping, in its frame.</summary>
    [ErbTemplate("rooms/directs/new.html.erb.cs")]
    public partial void RoomsDirectsNew(HtmlWriter w);

    /// <summary><c>rooms/directs/edit</c>: who is in a direct room, and its delete button.</summary>
    [ErbTemplate("rooms/directs/edit.html.erb.cs")]
    public partial void RoomsDirectsEdit(HtmlWriter w, DirectRoomSettings room);

    /// <summary>
    /// <c>users/autocompletables/_template</c> (its own task's template, not yet ported): the
    /// pill the autocomplete fills in for each picked user.
    /// </summary>
    [ErbTemplate("rooms/directs/_autocompletable_template.html.erb.cs")]
    public partial void UsersAutocompletablesTemplateStandIn(HtmlWriter w);
}
#pragma warning restore IDE0060

/// <summary>What <c>rooms/directs/edit</c> reads.</summary>
/// <param name="Id">The room's id.</param>
/// <param name="DisplayName"><c>room_display_name(@room)</c>.</param>
/// <param name="Users">
/// <c>@room.users.many? ? @room.users.without(Current.user) : @room.users</c>.
/// </param>
/// <param name="LastRoomId"><c>last_room_visited</c>'s id, for the back link.</param>
public sealed record DirectRoomSettings(long Id, string? DisplayName, IReadOnlyList<RoomMember> Users, long? LastRoomId);
