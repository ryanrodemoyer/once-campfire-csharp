using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/rooms/involvements/show.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>rooms/involvements/show</c>: the bell's frame, holding the button that cycles the
    /// user's <paramref name="involvement"/> in the room.
    /// </summary>
    [ErbTemplate("rooms/involvements/show.html.erb.cs")]
    public partial void RoomsInvolvementsShow(HtmlWriter w, Room room, string involvement);
}
#pragma warning restore IDE0060
