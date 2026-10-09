namespace Campfire.Web.Helpers;

// reference/app/views/autocompletable/users and the mention markup it renders
// (reference/app/views/users/_mention.html.erb, inlined here).
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>autocompletable/users/index</c>: one prompt item per user, with no layout.</summary>
    [ErbTemplate("autocompletable/users/index.html.erb.cs")]
    public partial void AutocompletableUsersIndex(HtmlWriter w, IReadOnlyList<AutocompletableUser> users);

    /// <summary><c>autocompletable/users/_prompt_item</c>: a mention the editor can insert.</summary>
    [ErbTemplate("autocompletable/users/_prompt_item.html.erb.cs")]
    public partial void AutocompletableUsersPromptItem(HtmlWriter w, AutocompletableUser user);

    /// <summary><c>users/_mention</c>: the inline span the editor keeps for a mentioned user.</summary>
    [ErbTemplate("autocompletable/users/_mention.html.erb.cs")]
    public partial void AutocompletableUsersMention(HtmlWriter w, AutocompletableUser user);
}
#pragma warning restore IDE0060

/// <summary>A user the autocomplete templates render. <paramref name="Sgid"/> is <c>attachable_sgid</c>.</summary>
public sealed record AutocompletableUser(long Id, string Name, string Title, string Sgid, string AvatarToken, DateTimeOffset UpdatedAt);
