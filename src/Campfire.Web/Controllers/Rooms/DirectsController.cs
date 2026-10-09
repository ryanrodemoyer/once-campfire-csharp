using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Rooms::DirectsController</c> (reference/app/controllers/rooms/directs_controller.rb): the
/// form for starting a Ping, finding or creating the direct room for a set of users, its settings
/// page and deleting it, which any of its users may do. Other room types are out of its reach.
/// </summary>
public sealed class RoomsDirectsController : RoomSettingsController
{
    static readonly ControllerCallbacks<RoomsDirectsController> Chain = RoomsCallbacks<RoomsDirectsController>()
        .Before("set_room", c => c.SetRoomAsync(), only: ["edit", "destroy"]);

    public static readonly RequestDelegate New = Action(Chain, c => c.RenderPageAsync((view, w) => view.RoomsDirectsNew(w)));

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Edit = Action(Chain, c => c.EditAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    /// <summary>
    /// <c>show</c>, inherited from RoomsController: <c>remember_last_room_visited</c> still runs
    /// for it, but <c>set_room</c> doesn't here, so it fails on the missing room.
    /// </summary>
    public static readonly RequestDelegate Show = Action(Chain, c => c.RedirectToRoom());

    // create: `Rooms::Direct.find_or_create_for(selected_users)`
    async ValueTask CreateAsync()
    {
        var selectedUserIds = RoomSettingsParams.UserIds(Params).Append(User.Id).ToList();
        var (userId, now) = (User.Id, Now);
        var direct = await WriteAsync(transaction =>
        {
            var session = transaction.Session;
            // `selected_users`: `User.where(id: selected_users_ids.including(Current.user.id))`
            var selected = Users.WhereIds(session, selectedUserIds).Select(user => user.Id).ToList();
            return Rooms.FindDirectFor(session, selected) ?? Rooms.CreateFor(session, RoomType.Direct, null, userId, selected, now);
        }).ConfigureAwait(false);

        // broadcast_create_room: each member's own sidebar link to it.
        var broadcasts = await ReadAsync(session => Memberships.ForRoom(session, direct.Id)
            .Select(membership => (membership.UserId, Html: RenderDirectRoom(session, direct, membership))).ToList()).ConfigureAwait(false);
        foreach (var (memberId, html) in broadcasts)
        {
            BroadcastPrepend(UserRooms(memberId), "direct_rooms", html);
        }
        RedirectTo(Routes.RoomUrl(UrlBase, direct.Id));
    }

    // edit: the room's other users (or just the user, alone in it).
    async ValueTask EditAsync()
    {
        var lastRoomId = await LastRoomIdAsync().ConfigureAwait(false);
        var (current, user) = (CurrentRoom, User);
        var settings = await ReadAsync(session =>
        {
            var users = Users.InRoom(session, current.Id);
            // `@room.users.many? ? @room.users.without(Current.user) : @room.users`
            var shown = users.Count > 1 ? users.Where(member => member.Id != user.Id).ToList() : users;
            var displayName = View.RoomDisplayName(current.Name, isDirect: true, users.Select(member => (member.Id, member.Name)), user.Id, user.Name);
            return new DirectRoomSettings(current.Id, displayName, Members(shown), lastRoomId);
        }).ConfigureAwait(false);
        await RenderPageAsync((view, w) => view.RoomsDirectsEdit(w, settings)).ConfigureAwait(false);
    }

    // All users in a direct room can administer it. Only direct rooms, though: this relaxation
    // is why the room scope below has to keep every other type out of reach.
    internal override void EnsureCanAdministerRoom()
    {
    }

    private protected override Room? FindInScope(SqliteSession session, long userId, long roomId) =>
        Rooms.FindForUser(session, userId, roomId) is { IsDirect: true } direct ? direct : null;
}
