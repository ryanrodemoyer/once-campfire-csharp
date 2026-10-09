using Campfire.Data.Queries;
using Campfire.RailsCompat.Ruby;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Accounts::Bots::KeysController</c> (reference/app/controllers/accounts/bots/keys_controller.rb):
/// regenerating a bot's API key.
/// </summary>
public sealed class AccountsBotsKeysController : ApplicationController
{
    static readonly ControllerCallbacks<AccountsBotsKeysController> Chain = Callbacks.For<AccountsBotsKeysController>()
        .EnsureCanAdminister();

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    async ValueTask UpdateAsync()
    {
        var param = Params["bot_id"];
        var id = param is string text ? ActiveModelInteger.Cast(text) : null;
        var bot = (id is { } botId ? await ReadAsync(session => Users.FindActiveBot(session, botId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={param}");

        var now = Now;
        await WriteAsync(tx => Users.ResetBotKey(tx.Session, bot, now)).ConfigureAwait(false);
        RedirectTo(Routes.AccountBotsUrl(UrlBase));
    }
}
