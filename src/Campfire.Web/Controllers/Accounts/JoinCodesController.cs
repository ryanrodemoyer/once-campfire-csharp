using Campfire.Data.Queries;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Accounts::JoinCodesController</c> (reference/app/controllers/accounts/join_codes_controller.rb):
/// an administrator replaces the join link.
/// </summary>
public sealed class AccountsJoinCodesController : ApplicationController
{
    static readonly ControllerCallbacks<AccountsJoinCodesController> Chain = Callbacks.For<AccountsJoinCodesController>()
        .EnsureCanAdminister();

    /// <summary><c>Current.account.reset_join_code</c>, then back to the settings.</summary>
    public static readonly RequestDelegate Create = Action(Chain, async c =>
    {
        var now = c.Now;
        await c.WriteAsync(tx =>
        {
            var account = Accounts.First(tx.Session) ?? throw new InvalidOperationException("undefined method 'reset_join_code' for nil");
            Accounts.ResetJoinCode(tx.Session, account, now);
        });
        c.RedirectTo(Routes.EditAccountUrl(c.UrlBase));
    });
}
