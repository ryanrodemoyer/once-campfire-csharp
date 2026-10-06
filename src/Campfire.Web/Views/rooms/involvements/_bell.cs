using Campfire.Data.Records;
using Campfire.RailsCompat.UserAgent;

namespace Campfire.Web.Helpers;

// reference/app/views/rooms/involvements/_bell.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>rooms/involvements/_bell</c>: the frame that loads the room's involvement button once
    /// notifications are ready, and the help shown when they aren't allowed.
    /// </summary>
    [ErbTemplate("rooms/involvements/_bell.html.erb.cs")]
    public partial void RoomsInvolvementsBell(HtmlWriter w, Room room, ApplicationPlatform platform);
}
#pragma warning restore IDE0060
