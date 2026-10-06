using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Ruby;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Pipeline;

/// <summary>
/// The reference controllers the auth matrix reaches, declared with the same callbacks as their
/// files in reference/app/controllers. Actions the matrix never reaches throw, so a request that
/// gets through a chain it shouldn't fails the comparison.
/// </summary>
static class MatrixControllers
{
    public static RouteTable Table { get; } = new(Routes.Definitions, definition => definition.Endpoint switch
    {
        "welcome#show" => WelcomeController.Show,
        "rooms#show" => RoomsController.Show,
        "accounts/join_codes#create" => JoinCodesController.Create,
        "users#new" => UsersController.New,
        "first_runs#show" => FirstRunsController.Show,
        "messages/by_bots#create" => ByBotsController.Create,
        "rooms/involvements#show" => InvolvementsController.Show,
        "rooms/involvements#update" => InvolvementsController.Update,
        _ => null,
    });

    /// <summary>An action or callback the matrix never gets to.</summary>
    public static ValueTask Unreached(Controller controller) =>
        throw new InvalidOperationException($"{controller.ControllerPath}#{controller.ActionName} isn't part of the matrix");
}

/// <summary>Stands in for A01's <c>sessions/incompatible_browser</c>: a page whose layout asks for a CSRF token.</summary>
class MatrixController : ApplicationController
{
    protected override ValueTask RenderIncompatibleBrowserAsync()
    {
        Render($"<meta name=\"csrf-token\" content=\"{FormAuthenticityToken()}\">", "text/html");
        return ValueTask.CompletedTask;
    }
}

// reference/app/controllers/welcome_controller.rb
sealed class WelcomeController : MatrixController
{
    static readonly ControllerCallbacks<WelcomeController> Chain = Callbacks.For<WelcomeController>();

    public static readonly RequestDelegate Show = Action(Chain, async (WelcomeController c) =>
    {
        var userId = c.Current.User!.Id;
        if ((await c.ReadAsync(session => Rooms.ForUser(session, userId))).Count > 0)
        {
            var room = await c.LastRoomVisitedAsync();
            c.RedirectTo(Routes.RoomUrl(c.UrlBase, room!.Id));
        }
        else
        {
            throw new InvalidOperationException("welcome#show's page isn't part of the matrix");
        }
    });
}

// reference/app/controllers/rooms_controller.rb
sealed class RoomsController : MatrixController
{
    static readonly ControllerCallbacks<RoomsController> Chain = Callbacks.For<RoomsController>()
        .Before("set_room", c => c.SetRoomAsync(), only: ["show", "destroy"])
        .Before("ensure_can_administer", c => c.EnsureCanAdministerRoom(), only: ["destroy"])
        .Before("remember_last_room_visited", c => c.RememberLastRoomVisited(c.room!.Id), only: ["show"]);

    Room? room;

    public static readonly RequestDelegate Show = Action(Chain, MatrixControllers.Unreached);

    async ValueTask SetRoomAsync()
    {
        var userId = Current.User!.Id;
        var id = (Params["room_id"] ?? Params["id"]) is string param ? ActiveModelInteger.Cast(param) : null;
        room = id is { } roomId ? await ReadAsync(session => Rooms.FindForUser(session, userId, roomId)) : null;
        if (room is null)
        {
            RedirectTo(Routes.RootUrl(UrlBase), alert: "Room not found or inaccessible");
        }
    }

    void EnsureCanAdministerRoom()
    {
        if (!Current.User!.CanAdminister(room!.CreatorId))
        {
            Head(403);
        }
    }
}

// reference/app/controllers/accounts/join_codes_controller.rb
sealed class JoinCodesController : MatrixController
{
    static readonly ControllerCallbacks<JoinCodesController> Chain = Callbacks.For<JoinCodesController>()
        .EnsureCanAdminister();

    public static readonly RequestDelegate Create = Action(Chain, async (JoinCodesController c) =>
    {
        var account = (await c.Current.AccountAsync())!;
        var now = c.Now;
        await c.WriteAsync(tx => Accounts.ResetJoinCode(tx.Session, account, now));
        c.RedirectTo(Routes.EditAccountUrl(c.UrlBase));
    });
}

// reference/app/controllers/users_controller.rb
sealed class UsersController : MatrixController
{
    static readonly ControllerCallbacks<UsersController> Chain = Callbacks.For<UsersController>()
        .RequireUnauthenticatedAccess(only: ["new", "create"])
        .Before("set_user", MatrixControllers.Unreached, only: ["show"])
        .Before("verify_join_code", c => c.VerifyJoinCodeAsync(), only: ["new", "create"]);

    public static readonly RequestDelegate New = Action(Chain, MatrixControllers.Unreached);

    async ValueTask VerifyJoinCodeAsync()
    {
        if ((await Current.AccountAsync())!.JoinCode != Params["join_code"] as string)
        {
            Head(404);
        }
    }
}

// reference/app/controllers/first_runs_controller.rb
sealed class FirstRunsController : MatrixController
{
    static readonly ControllerCallbacks<FirstRunsController> Chain = Callbacks.For<FirstRunsController>()
        .AllowUnauthenticatedAccess()
        .Before("prevent_repeats", c => c.PreventRepeatsAsync());

    public static readonly RequestDelegate Show = Action(Chain, MatrixControllers.Unreached);

    async ValueTask PreventRepeatsAsync()
    {
        if (await ReadAsync(Accounts.Count) > 0)
        {
            RedirectTo(Routes.RootUrl(UrlBase));
        }
    }
}

// reference/app/controllers/messages_controller.rb and messages/by_bots_controller.rb
sealed class ByBotsController : MatrixController
{
    static readonly ControllerCallbacks<ByBotsController> Chain = Callbacks.For<ByBotsController>()
        // MessagesController: include RoomScoped, then its own callbacks.
        .Before("set_room", c => c.SetRoomAsync())
        .Before("set_room", c => c.SetRoomAsync(), except: ["create"])
        .Before("set_message", MatrixControllers.Unreached, only: ["show", "edit", "update", "destroy"])
        .Before("ensure_can_administer", MatrixControllers.Unreached, only: ["edit", "update", "destroy"])
        // Messages::ByBotsController
        .AllowBotAccess(only: ["index", "create", "update", "destroy"])
        .Before("set_room", c => c.SetRoomAsync())
        .Before("set_message", MatrixControllers.Unreached, only: ["update", "destroy"])
        .Before("ensure_can_administer", MatrixControllers.Unreached, only: ["update", "destroy"])
        .Before("ensure_body_or_attachment_present", c => c.EnsureBodyOrAttachmentPresent(), only: ["create"]);

    Room? room;

    public static readonly RequestDelegate Create = Action(Chain, MatrixControllers.Unreached);

    // Messages::ByBotsController#set_room
    async ValueTask SetRoomAsync()
    {
        var userId = Current.User!.Id;
        var id = Params["room_id"] is string param ? ActiveModelInteger.Cast(param) : null;
        room = id is { } roomId ? await ReadAsync(session => Rooms.FindForUser(session, userId, roomId)) : null;
        if (room is null)
        {
            Head(404);
        }
    }

    void EnsureBodyOrAttachmentPresent()
    {
        if (Params["attachment"] is null or "" && string.IsNullOrWhiteSpace(System.Text.Encoding.UTF8.GetString(Request.Body.Raw)))
        {
            Head(422);
        }
    }
}

// reference/app/controllers/rooms/involvements_controller.rb
sealed class InvolvementsController : MatrixController
{
    static readonly ControllerCallbacks<InvolvementsController> Chain = Callbacks.For<InvolvementsController>()
        .Before("set_room", async c => c.Room = await RoomScoped.FindRoomAsync(c));

    public (Membership Membership, Room Room)? Room { get; private set; }

    public static readonly RequestDelegate Show = Action(Chain, MatrixControllers.Unreached);

    public static readonly RequestDelegate Update = Action(Chain, MatrixControllers.Unreached);
}
