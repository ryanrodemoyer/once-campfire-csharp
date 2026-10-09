using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Ruby;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Users::BansController</c> (reference/app/controllers/users/bans_controller.rb): an
/// administrator bans someone (their public addresses, their sessions and connections, and later
/// their messages, through RemoveBannedContentJob) or lifts the ban.
/// </summary>
public sealed class UsersBansController : ApplicationController
{
    static readonly ControllerCallbacks<UsersBansController> Chain = Callbacks.For<UsersBansController>()
        .EnsureCanAdminister()
        .Before("set_user", c => c.SetUserAsync());

    User? user;

    User BannedUser => user ?? throw new InvalidOperationException("set_user hasn't run");

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    // `@user.ban`, then the person's page. A session from an address that isn't public fails the
    // ban's validation (ActiveRecord::RecordInvalid, a 422) and nothing is banned.
    async ValueTask CreateAsync()
    {
        var (banned, now, seams) = (BannedUser, Now, App.RequireSeams());
        try
        {
            await WriteAsync(tx => UserLifecycle.Ban(tx, seams, banned, now)).ConfigureAwait(false);
        }
        catch (RecordInvalidException error)
        {
            throw new UnprocessableRecordException(error);
        }
        RedirectTo(Routes.UserUrl(UrlBase, banned.Id));
    }

    // `@user.unban`, then the person's page.
    async ValueTask DestroyAsync()
    {
        var (banned, now) = (BannedUser, Now);
        await WriteAsync(tx => UserLifecycle.Unban(tx, banned, now)).ConfigureAwait(false);
        RedirectTo(Routes.UserUrl(UrlBase, banned.Id));
    }

    // set_user: `User.find(params[:user_id])`, whatever their status.
    async ValueTask SetUserAsync()
    {
        var param = Params["user_id"];
        var id = param is string text ? ActiveModelInteger.Cast(text) : null;
        user = (id is { } userId ? await ReadAsync(session => Users.Find(session, userId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={param}");
    }

    /// <summary>
    /// <c>ActiveRecord::RecordInvalid</c> as <c>rescue_responses</c> answers it: 422.
    /// </summary>
    sealed class UnprocessableRecordException(Exception innerException) : Exception(innerException.Message, innerException), IHasHttpStatus
    {
        public int StatusCode => StatusCodes.Status422UnprocessableEntity;
    }
}
