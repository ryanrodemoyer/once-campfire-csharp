using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/accounts/bots/{index,new,edit,_form,_bot}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>accounts/bots/index</c>: the account's active chat bots and their curl commands.</summary>
    [ErbTemplate("accounts/bots/index.html.erb.cs")]
    public partial void AccountsBotsIndex(HtmlWriter w, IReadOnlyList<BotRow> bots);

    /// <summary><c>accounts/bots/_bot</c>: one bot row with its curl commands per room.</summary>
    [ErbTemplate("accounts/bots/_bot.html.erb.cs")]
    public partial void AccountsBotsBot(HtmlWriter w, BotRow bot);

    /// <summary><c>accounts/bots/new</c>: form for a new bot.</summary>
    [ErbTemplate("accounts/bots/new.html.erb.cs")]
    public partial void AccountsBotsNew(HtmlWriter w, FormModel bot);

    /// <summary><c>accounts/bots/edit</c>: form for editing a bot, plus delete and regenerate key buttons.</summary>
    [ErbTemplate("accounts/bots/edit.html.erb.cs")]
    public partial void AccountsBotsEdit(HtmlWriter w, BotEditModel bot);

    /// <summary><c>accounts/bots/_form</c>: bot avatar, name and webhook URL fields.</summary>
    [ErbTemplate("accounts/bots/_form.html.erb.cs")]
    public partial void AccountsBotsForm(HtmlWriter w, FormBuilder form, BotFormModel bot);
}
#pragma warning restore IDE0060

/// <summary>One bot row shown on <c>accounts/bots/index</c>.</summary>
public sealed record BotRow(
    User User,
    AvatarUser Avatar,
    IReadOnlyList<Room> Rooms);

/// <summary>What <c>accounts/bots/edit</c> shows.</summary>
public sealed record BotEditModel(
    User User,
    FormModel FormModel,
    BotFormModel Form);

/// <summary>What <c>accounts/bots/_form</c> shows for avatar preview.</summary>
public sealed record BotFormModel(
    string? AvatarUrl);
