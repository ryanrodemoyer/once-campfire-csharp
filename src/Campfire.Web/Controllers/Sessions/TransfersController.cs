using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Signing;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Sessions::TransfersController</c> (reference/app/controllers/sessions/transfers_controller.rb):
/// a sign-in link (<c>session_transfer_url(user.transfer_id)</c>, good for four hours) whose page
/// puts itself back, signing in as its user on another device.
/// </summary>
public sealed class SessionsTransfersController : ApplicationController
{
    static readonly ControllerCallbacks<SessionsTransfersController> Chain = Callbacks.For<SessionsTransfersController>()
        .AllowUnauthenticatedAccess();

    /// <summary><c>sessions/transfers/show</c>: a form that submits itself.</summary>
    public static readonly RequestDelegate Show = Action(Chain, c => c.RenderActionAsync((view, _, w) => view.SessionsTransfersShow(w)));

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    // A new session for `User.active.find_by_transfer_id(params[:id])`, else 400.
    async ValueTask UpdateAsync()
    {
        if (await FindActiveByTransferIdAsync().ConfigureAwait(false) is { } user)
        {
            await StartNewSessionForAsync(user).ConfigureAwait(false);
            RedirectTo(PostAuthenticatingUrl());
        }
        else
        {
            Head(400);
        }
    }

    // `User.active.find_by_transfer_id(id)`: `find_signed(id, purpose: :transfer)` in the active
    // scope, nil for a bad or expired signature.
    async ValueTask<User?> FindActiveByTransferIdAsync()
    {
        if (Params["id"] is not string transferId || TransferableUser.VerifyTransferId(App.Keys, transferId, Now) is not { } id)
        {
            return null;
        }
        return await ReadAsync(session => Users.FindActive(session, id)).ConfigureAwait(false);
    }
}
