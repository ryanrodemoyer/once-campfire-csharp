namespace Campfire.Web.Helpers;

// reference/app/views/accounts/users/index.turbo_stream.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>accounts/users/index.turbo_stream</c>: a page of people in place of the next-page frame,
    /// and a frame for the page after it unless this is the last.
    /// </summary>
    [ErbTemplate("accounts/users/index.turbo_stream.erb.cs")]
    public partial void AccountsUsersIndex(HtmlWriter w, AccountUsersPage page);
}
#pragma warning restore IDE0060

/// <summary>What <c>accounts/users/index</c> shows.</summary>
/// <param name="Users"><c>@page.records</c>, each with <c>user.avatar_token</c>.</param>
/// <param name="NextPage"><c>@page.next_param</c>, or null on the last page.</param>
public sealed record AccountUsersPage(IReadOnlyList<AccountUserRow> Users, object? NextPage);
