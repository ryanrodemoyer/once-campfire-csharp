using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/accounts/edit.html.erb, accounts/custom_styles/edit.html.erb and
// accounts/users/{_user,_next_page_container}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>accounts/edit</c>: the account's name, logo and settings, its join link and its people.</summary>
    [ErbTemplate("accounts/edit.html.erb.cs")]
    public partial void AccountsEdit(HtmlWriter w, AccountEditPage page);

    /// <summary><c>accounts/custom_styles/edit</c>: the account's custom CSS.</summary>
    [ErbTemplate("accounts/custom_styles/edit.html.erb.cs")]
    public partial void AccountsCustomStylesEdit(HtmlWriter w, FormModel account);

    /// <summary><c>accounts/users/_user</c>: a person on the account, with the administrator's role and removal buttons.</summary>
    [ErbTemplate("accounts/users/_user.html.erb.cs")]
    public partial void AccountsUsersUser(HtmlWriter w, User user, string avatarToken);

    /// <summary><c>accounts/users/_next_page_container</c>: the lazy frame that loads the next page of people.</summary>
    [ErbTemplate("accounts/users/_next_page_container.html.erb.cs")]
    public partial void AccountsUsersNextPageContainer(HtmlWriter w, object page);

    /// <summary><c>form_with model: @account</c>: the account's form, with the attributes its fields read.</summary>
    public static FormModel AccountFormModel(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new(new(nameof(Account), account.Id), true, new Dictionary<string, object?>
        {
            ["name"] = account.Name,
            ["custom_styles"] = account.CustomStyles,
        });
    }

    /// <summary><c>form_with model: user</c> in <c>accounts/users/_user</c>: the role its check box reads.</summary>
    internal static FormModel AccountUserFormModel(User user) =>
        new(new(User.ModelName, user.Id), true, new Dictionary<string, object?>
        {
            ["role"] = user.Role.ToString().ToLowerInvariant(),
        });
}
#pragma warning restore IDE0060

/// <summary>A person on <c>accounts/edit</c>, with <c>user.avatar_token</c> for their avatar.</summary>
public sealed record AccountUserRow(User User, string AvatarToken);

/// <summary>What <c>accounts/edit</c> shows.</summary>
/// <param name="Account">The account's form model.</param>
/// <param name="AccountFormPath">
/// Where <c>form_with model: @account</c> posts: <c>polymorphic_path(@account)</c> for the singular
/// <c>resource :account</c> is <c>account_path(@account)</c>, whose id lands in the format
/// (<c>/account.1</c>).
/// </param>
/// <param name="AccountName"><c>@account.name</c>.</param>
/// <param name="JoinCode"><c>Current.account.join_code</c>, for <c>accounts/_invite</c>.</param>
/// <param name="RestrictRoomCreationToAdministrators"><c>Current.account.settings.restrict_room_creation_to_administrators?</c>.</param>
/// <param name="CurrentUserIsAdministrator"><c>Current.user.administrator?</c>.</param>
/// <param name="LastRoomId"><c>last_room_visited</c>'s id, for the back link.</param>
/// <param name="Administrators">The administrators of <c>account_users.ordered.without_bots</c>.</param>
/// <param name="Members">Everyone else in it.</param>
/// <param name="NextPage"><c>@page.next_param</c>, or null on the last page.</param>
public sealed record AccountEditPage(
    FormModel Account,
    string AccountFormPath,
    string AccountName,
    string JoinCode,
    bool RestrictRoomCreationToAdministrators,
    bool CurrentUserIsAdministrator,
    long? LastRoomId,
    IReadOnlyList<AccountUserRow> Administrators,
    IReadOnlyList<AccountUserRow> Members,
    object? NextPage);
