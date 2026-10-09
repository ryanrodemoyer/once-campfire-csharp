using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Web.Broadcasts;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Rooms::ClosedsController</c> (reference/app/controllers/rooms/closeds_controller.rb): new
/// and edit pages for closed rooms, creating one for the users picked, and updating one (its
/// name and who is in it), which turns an open room closed. Direct rooms are out of its reach.
/// </summary>
public sealed class RoomsClosedsController : RoomSettingsController
{
    // `DEFAULT_ROOM_NAME`
    const string defaultRoomName = "New room";

    static readonly ControllerCallbacks<RoomsClosedsController> Chain = RoomsCallbacks<RoomsClosedsController>()
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

    // The type `@room` is edited and saved as: `force_room_type` makes it closed.
    RoomType? type;

    async ValueTask NewAsync()
    {
        var lastRoomId = await LastRoomIdAsync().ConfigureAwait(false);
        var users = await ReadAsync(Users.ActiveOrdered).ConfigureAwait(false);
        var form = new RoomForm(RoomType.Closed, null, defaultRoomName, CanAdminister(null), Routes.NewRoomsOpenPath(), lastRoomId);
        await RenderPageAsync((view, w) => view.RoomsClosedsNew(w, form, Members(users))).ConfigureAwait(false);
    }

    // create: `Rooms::Closed.create_for(room_params, users: grantees)`
    async ValueTask CreateAsync()
    {
        var name = RoomSettingsParams.Name(Params) is { } assigned ? assigned.Value : null;
        var granteeIds = RoomSettingsParams.UserIds(Params);
        var (userId, now) = (User.Id, Now);
        var created = await WriteAsync(transaction =>
            Rooms.CreateFor(transaction.Session, RoomType.Closed, name, userId, Grantees(transaction.Session, granteeIds), now)).ConfigureAwait(false);

        // broadcast_create_room: the room's sidebar link, prepended for each of its users.
        await EachUserAndHtmlForAsync(created, (user, html) => BroadcastPrepend(UserRooms(user.Id), "shared_rooms", html)).ConfigureAwait(false);
        RedirectTo(Routes.RoomUrl(UrlBase, created.Id));
    }

    // edit: the room's users first (`@selected_users`), then everyone else active
    // (`@unselected_users`), each in `User.active.ordered`.
    async ValueTask EditAsync()
    {
        var lastRoomId = await LastRoomIdAsync().ConfigureAwait(false);
        var roomId = CurrentRoom.Id;
        var (selected, unselected) = await ReadAsync(session =>
        {
            var selectedUserIds = Users.IdsInRoom(session, roomId).ToHashSet();
            var users = Users.ActiveOrdered(session);
            return (users.Where(user => selectedUserIds.Contains(user.Id)).ToList(), users.Where(user => !selectedUserIds.Contains(user.Id)).ToList());
        }).ConfigureAwait(false);
        var current = CurrentRoom;
        var form = new RoomForm(RoomType.Closed, current.Id, current.Name, CanAdminister(current), Routes.EditRoomsOpenPath(current.Id), lastRoomId, current.Name);
        await RenderPageAsync((view, w) => view.RoomsClosedsEdit(w, form, Members(selected), Members(unselected))).ConfigureAwait(false);
    }

    // update: `@room.update! room_params` as a closed room, then
    // `@room.memberships.revise(granted: grantees, revoked: revokees)`, each in its own
    // transaction.
    async ValueTask UpdateAsync()
    {
        var name = RoomSettingsParams.Name(Params);
        var granteeIds = RoomSettingsParams.UserIds(Params);
        var (stored, saveAs, now, seams) = (CurrentRoom, type, Now, App.RequireSeams());
        var updated = await WriteAsync(transaction => Rooms.Update(transaction.Session, stored, now, name, saveAs)).ConfigureAwait(false);
        await WriteAsync(transaction =>
        {
            var session = transaction.Session;
            // `revokees`: `@room.users.where.not(id: grantee_ids)`
            var revokees = Users.IdsInRoom(session, updated.Id).Where(id => !granteeIds.Contains(id)).ToList();
            MembershipLifecycle.Revise(transaction, seams, updated, Grantees(session, granteeIds), revokees);
        }).ConfigureAwait(false);

        // broadcast_update_room: the room's sidebar link, replaced for each of its users.
        var target = ListDomId(updated);
        await EachUserAndHtmlForAsync(updated, (user, html) => TurboBroadcasts.Replace(App.RequireSeams().Broadcaster, UserRooms(user.Id), target, html)).ConfigureAwait(false);
        RedirectTo(Routes.RoomUrl(UrlBase, updated.Id));
    }

    // Allows us to edit an open room and turn it into a closed one on saving
    // (`@room.becomes!(Rooms::Closed)`).
    void ForceRoomType() => type = RoomType.Closed;

    // each_user_and_html_for: the shared sidebar partial, rendered once, for each of the room's users.
    async ValueTask EachUserAndHtmlForAsync(Room subject, Action<User, string> broadcast)
    {
        var (html, users) = await ReadAsync(session => (RenderSharedRoom(session, subject), Users.InRoom(session, subject.Id))).ConfigureAwait(false);
        foreach (var user in users)
        {
            broadcast(user, html);
        }
    }

    // `grantees`: `User.where(id: grantee_ids)`, in the table's order.
    static List<long> Grantees(SqliteSession session, List<long> granteeIds) => [.. Users.WhereIds(session, granteeIds).Select(user => user.Id)];

    // Open and closed rooms convert into each other, so both are in reach here. Direct rooms
    // never are: converting one would let its creator revise who's in it.
    private protected override Room? FindInScope(SqliteSession session, long userId, long roomId) =>
        Rooms.FindForUserWithoutDirects(session, userId, roomId);
}
