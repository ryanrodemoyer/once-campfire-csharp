namespace Campfire.Web.Helpers;

// reference/app/views/accounts/_invite.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>accounts/_invite</c>: the join link to copy, share or scan, and (for administrators) regenerate.</summary>
    [ErbTemplate("accounts/_invite.html.erb.cs")]
    public partial void AccountsInvite(HtmlWriter w, string? joinCode);
}
#pragma warning restore IDE0060
