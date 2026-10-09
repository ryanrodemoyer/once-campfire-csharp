namespace Campfire.Web.Helpers;

// reference/app/views/rooms/closeds/{new,edit,_form,_user}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>rooms/closeds/new</c>: the form for a new closed room, with everyone to pick from.</summary>
    [ErbTemplate("rooms/closeds/new.html.erb.cs")]
    public partial void RoomsClosedsNew(HtmlWriter w, RoomForm room, IReadOnlyList<RoomMember> users);

    /// <summary><c>rooms/closeds/edit</c>: a closed room's settings, its members first.</summary>
    [ErbTemplate("rooms/closeds/edit.html.erb.cs")]
    public partial void RoomsClosedsEdit(HtmlWriter w, RoomForm room, IReadOnlyList<RoomMember> selectedUsers, IReadOnlyList<RoomMember> unselectedUsers);

    /// <summary><c>rooms/closeds/_form</c>: the room's name and who may be in it, with the switch to an open room.</summary>
    [ErbTemplate("rooms/closeds/_form.html.erb.cs")]
    public partial void RoomsClosedsForm(HtmlWriter w, RoomForm room, IReadOnlyList<RoomMember> selectedUsers, IReadOnlyList<RoomMember> unselectedUsers);

    /// <summary><c>rooms/closeds/_user</c>: one user, with the switch giving them access (<paramref name="selected"/>: they have it).</summary>
    [ErbTemplate("rooms/closeds/_user.html.erb.cs")]
    public partial void RoomsClosedsUser(HtmlWriter w, RoomMember user, RoomForm room, bool selected);
}
#pragma warning restore IDE0060
