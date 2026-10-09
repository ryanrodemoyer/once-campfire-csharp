using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Web.Broadcasts;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Rooms::OpensController</c> (reference/app/controllers/rooms/opens_controller.rb): new and
/// edit pages for open rooms, creating one (everyone joins), and updating one, which turns a
/// closed room open. Direct rooms are out of its reach.
/// </summary>
public sealed class RoomsOpensController : RoomSettingsController
{
    // `DEFAULT_ROOM_NAME`
    const string defaultRoomName = "New room";

    static readonly ControllerCallbacks<RoomsOpensController> Chain = RoomsCallbacks<RoomsOpensController>()
        .Before("set_room", c => c.SetRoomAsync(), only: ["show", "edit", "update"])
        .Before("ensure_can_administer", c => c.EnsureCanAdministerRoom(), only: ["update"])
        .Before("remember_last_room_visited", c => c.RememberLastRoomVisited(c.CurrentRoom.Id), only: ["show"])
        .Before("force_room_type", c => c.ForceRoomType(), only: ["edit", "update"])
        .Before("ensure_permission_to_create_rooms", c => c.EnsurePermissionToCreateRoomsAsync(), only: ["new", "create"]);

    public static readonly RequestDelegate Show = Action(Chain, c => c.RedirectToRoom());

    public static readonly RequestDelegate New = Action(Chain, c => c.NewAsync());

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Edit = Action(Chain, c => c.EditAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    // The type `@room` is edited and saved as: `force_room_type` makes it open.
    RoomType? type;

    async ValueTask NewAsync()
    {
        var lastRoomId = await LastRoomIdAsync().ConfigureAwait(false);
        var users = await ReadAsync(Users.ActiveOrdered).ConfigureAwait(false);
        var form = new RoomForm(RoomType.Open, null, defaultRoomName, CanAdminister(null), Routes.NewRoomsClosedPath(), lastRoomId);
        await RenderPageAsync((view, w) => view.RoomsOpensNew(w, form, Members(users))).ConfigureAwait(false);
    }

    // create: `Rooms::Open.create_for(room_params, users: Current.user)`, then everyone active
    // joins once it commits (`grant_access_to_all_users`).
    async ValueTask CreateAsync()
    {
        var name = RoomParams() is { } assigned ? assigned.Value : null;
        var (userId, now) = (User.Id, Now);
        var created = await WriteAsync(transaction =>
        {
            var created = Rooms.CreateFor(transaction.Session, RoomType.Open, name, userId, [userId], now);
            transaction.AfterCommit(session => GrantAccessToAllUsers(session, created));
            return created;
        }).ConfigureAwait(false);

        // broadcast_create_room
        BroadcastPrepend(["rooms"], "shared_rooms", await ReadAsync(session => RenderSharedRoom(session, created)).ConfigureAwait(false));
        RedirectTo(Routes.RoomUrl(UrlBase, created.Id));
    }

    async ValueTask EditAsync()
    {
        var lastRoomId = await LastRoomIdAsync().ConfigureAwait(false);
        var users = await ReadAsync(Users.ActiveOrdered).ConfigureAwait(false);
        var form = Form(lastRoomId);
        await RenderPageAsync((view, w) => view.RoomsOpensEdit(w, form, Members(users))).ConfigureAwait(false);
    }

    // update: `@room.update! room_params` as an open room; a closed room turning open lets
    // everyone active in once it commits.
    async ValueTask UpdateAsync()
    {
        var name = RoomParams();
        var (stored, saveAs, now) = (CurrentRoom, type, Now);
        var updated = await WriteAsync(transaction =>
        {
            var updated = Rooms.Update(transaction.Session, stored, now, name, saveAs);
            if (stored.Type != RoomType.Open)
            {
                transaction.AfterCommit(session => GrantAccessToAllUsers(session, updated));
            }
            return updated;
        }).ConfigureAwait(false);

        // broadcast_update_room
        var html = await ReadAsync(session => RenderSharedRoom(session, updated)).ConfigureAwait(false);
        TurboBroadcasts.Replace(App.RequireSeams().Broadcaster, ["rooms"], ListDomId(updated), html);
        RedirectTo(Routes.RoomUrl(UrlBase, updated.Id));
    }

    // Allows us to edit a closed room and turn it into an open one on saving
    // (`@room.becomes!(Rooms::Open)`).
    void ForceRoomType() => type = RoomType.Open;

    // `room_params`: `params.require(:room).permit(:name)`; null when it holds no name.
    Change<string?>? RoomParams() => RoomSettingsParams.Name(Params);

    // Rooms::Open's `grant_access_to_all_users`: `memberships.grant_to(User.active)`.
    static void GrantAccessToAllUsers(SqliteSession session, Room open) =>
        Memberships.GrantTo(session, open, [.. Users.ActiveIds(session)]);

    RoomForm Form(long? lastRoomId)
    {
        var current = CurrentRoom;
        return new RoomForm(RoomType.Open, current.Id, current.Name, CanAdminister(current), Routes.EditRoomsClosedPath(current.Id), lastRoomId, current.Name);
    }

    // Open and closed rooms convert into each other, so both are in reach here. Direct rooms
    // never are: promoting one would republish its history to the whole account.
    private protected override Room? FindInScope(SqliteSession session, long userId, long roomId) =>
        Rooms.FindForUserWithoutDirects(session, userId, roomId);
}
