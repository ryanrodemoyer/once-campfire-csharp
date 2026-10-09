using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/users/avatars/show.svg.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>users/avatars/show.svg</c>: the user's initials on a colour picked from their id, squeezed
    /// to fit when there are three or more.
    /// </summary>
    [ErbTemplate("users/avatars/show.svg.erb.cs")]
    public static partial void UsersAvatarsShow(HtmlWriter w, User user);
}
#pragma warning restore IDE0060
