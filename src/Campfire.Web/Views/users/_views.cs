using Campfire.Data.Records;
using Campfire.Web.Controllers;

namespace Campfire.Web.Helpers;

// reference/app/views/users/{new,show,_ban_button}.html.erb and users/profiles/_transfer.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>users/new</c>: the sign-up form a join link opens.</summary>
    [ErbTemplate("users/new.html.erb.cs")]
    public partial void UsersNew(HtmlWriter w, string joinCode, string accountName, HelpContact? helpContact);

    /// <summary>
    /// <c>users/show</c>: a person's (or bot's) page. <paramref name="transferUrl"/> is
    /// <c>session_transfer_url(user.transfer_id)</c>, which administrators see for active users.
    /// </summary>
    [ErbTemplate("users/show.html.erb.cs")]
    public partial void UsersShow(HtmlWriter w, User user, string avatarToken, string? transferUrl);

    /// <summary><c>users/_ban_button</c>: ban an active user, or lift a ban.</summary>
    [ErbTemplate("users/_ban_button.html.erb.cs")]
    public partial void UsersBanButton(HtmlWriter w, User user);

    /// <summary><c>users/profiles/_transfer</c>: the sign-in link for another device.</summary>
    [ErbTemplate("users/profiles/_transfer.html.erb.cs")]
    public partial void UsersProfilesTransfer(HtmlWriter w, long userId, string transferUrl);
}
#pragma warning restore IDE0060
