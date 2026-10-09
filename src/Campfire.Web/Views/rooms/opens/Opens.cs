namespace Campfire.Web.Helpers;

// reference/app/views/rooms/opens/{new,edit,_form,_user}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>rooms/opens/new</c>: the form for a new open room, listing everyone it will include.</summary>
    [ErbTemplate("rooms/opens/new.html.erb.cs")]
    public partial void RoomsOpensNew(HtmlWriter w, RoomForm room, IReadOnlyList<RoomMember> users);

    /// <summary><c>rooms/opens/edit</c>: an open room's settings.</summary>
    [ErbTemplate("rooms/opens/edit.html.erb.cs")]
    public partial void RoomsOpensEdit(HtmlWriter w, RoomForm room, IReadOnlyList<RoomMember> users);

    /// <summary><c>rooms/opens/_form</c>: the room's name and everyone in it, with the switch to a closed room.</summary>
    [ErbTemplate("rooms/opens/_form.html.erb.cs")]
    public partial void RoomsOpensForm(HtmlWriter w, RoomForm room, IReadOnlyList<RoomMember> users);

    /// <summary><c>rooms/opens/_user</c>: one user of an open room.</summary>
    [ErbTemplate("rooms/opens/_user.html.erb.cs")]
    public partial void RoomsOpensUser(HtmlWriter w, RoomMember user, RoomForm room);
}
#pragma warning restore IDE0060
