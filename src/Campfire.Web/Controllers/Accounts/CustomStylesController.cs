using Campfire.Data.Queries;
using Campfire.RailsCompat.Params;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Accounts::CustomStylesController</c> (reference/app/controllers/accounts/custom_styles_controller.rb):
/// an administrator edits the CSS every page includes.
/// </summary>
public sealed class AccountsCustomStylesController : ApplicationController
{
    static readonly ControllerCallbacks<AccountsCustomStylesController> Chain = Callbacks.For<AccountsCustomStylesController>()
        .EnsureCanAdminister();

    static readonly PermitFilter[] AccountParams = [PermitFilter.Key("custom_styles")];

    /// <summary><c>accounts/custom_styles/edit</c>.</summary>
    public static readonly RequestDelegate Edit = Action(Chain, c => c.RenderActionAsync((view, session, w) =>
    {
        var account = Accounts.First(session) ?? throw new InvalidOperationException("undefined method 'model_name' for nil");
        view.AccountsCustomStylesEdit(w, View.AccountFormModel(account));
    }));

    /// <summary><c>@account.update!(account_params)</c>, then back to the editor with a "✓".</summary>
    public static readonly RequestDelegate Update = Action(Chain, async c =>
    {
        var attributes = c.Params.RequireHash("account").Permit(AccountParams);
        await AccountsController.UpdateAccountAsync(c.App, attributes, c.Now, c.RequestAborted);
        c.RedirectTo(Routes.EditAccountCustomStylesUrl(c.UrlBase), notice: "✓");
    });
}
