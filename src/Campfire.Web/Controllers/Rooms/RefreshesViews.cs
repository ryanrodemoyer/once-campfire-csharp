using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/rooms/refreshes/show.turbo_stream.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>rooms/refreshes/show.turbo_stream</c>: append messages created since, then replace
    /// messages updated since.
    /// </summary>
    [ErbTemplate("rooms/refreshes/show.turbo_stream.erb.cs")]
    public partial void RoomsRefreshesShow(HtmlWriter w, Room room, IReadOnlyList<MessageView> newMessages, IReadOnlyList<MessageView> updatedMessages);
}
#pragma warning restore IDE0060
