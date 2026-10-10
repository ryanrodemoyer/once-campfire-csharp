using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.RailsCompat.Signing;
using Campfire.RichText.Attachments;
using Campfire.Storage.Blobs;
using Campfire.Web.Broadcasts;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// What <c>Rooms::OpensController</c>, <c>Rooms::ClosedsController</c> and
/// <c>Rooms::DirectsController</c> inherit from <c>RoomsController</c>
/// (reference/app/controllers/rooms_controller.rb): <c>set_room</c> in the subclass's
/// <c>room_scope</c>, <c>ensure_can_administer</c>, <c>ensure_permission_to_create_rooms</c> and
/// <c>destroy</c>; and the pages and sidebar broadcasts the room settings render.
/// <see cref="RoomsDestroyController"/> is rooms#destroy itself.
/// </summary>
public abstract class RoomSettingsController : ApplicationController
{
    // `@room`: the room as stored, which `set_room` found.
    private protected Room? room;

    private protected Room CurrentRoom => room ?? throw new InvalidOperationException("undefined method for nil (@room)");

    private protected User User => Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");

    /// <summary>
    /// RoomsController's callbacks as a subclass inherits them. A subclass that declares one of
    /// them again replaces it, conditions and all, as Rails does.
    /// </summary>
    private protected static ControllerCallbacks<T> RoomsCallbacks<T>() where T : RoomSettingsController =>
        Callbacks.For<T>()
            .Before("set_room", c => c.SetRoomAsync(), only: ["show", "destroy"])
            .Before("ensure_can_administer", c => c.EnsureCanAdministerRoom(), only: ["destroy"])
            .Before("remember_last_room_visited", c => c.RememberLastRoomVisited(c.CurrentRoom.Id), only: ["show"]);

    /// <summary>
    /// <c>room_scope.find_by(id:)</c>: <c>Current.user.rooms</c>, which subclasses narrow to the
    /// room types they may act on.
    /// </summary>
    private protected virtual Room? FindInScope(SqliteSession session, long userId, long roomId) => Rooms.FindForUser(session, userId, roomId);

    // set_room: `room_scope.find_by(id: params[:room_id] || params[:id])`, else back to the root.
    internal async ValueTask SetRoomAsync()
    {
        var userId = User.Id;
        var id = (Params["room_id"] ?? Params["id"]) is string param ? ActiveModelInteger.Cast(param) : null;
        room = id is { } roomId ? await ReadAsync(session => FindInScope(session, userId, roomId)).ConfigureAwait(false) : null;
        if (room is null)
        {
            RedirectTo(Routes.RootUrl(UrlBase), alert: "Room not found or inaccessible");
        }
    }

    // ensure_can_administer: `head :forbidden unless Current.user.can_administer?(@room)`.
    internal virtual void EnsureCanAdministerRoom()
    {
        if (!User.CanAdminister(CurrentRoom.CreatorId))
        {
            Head(403);
        }
    }

    // ensure_permission_to_create_rooms
    internal async ValueTask EnsurePermissionToCreateRoomsAsync()
    {
        var restricted = await ReadAsync(session => Accounts.First(session)?.SettingsData.RestrictRoomCreationToAdministrators
            ?? throw new InvalidOperationException("undefined method 'settings' for nil")).ConfigureAwait(false);
        if (restricted && !User.IsAdministrator)
        {
            Head(403);
        }
    }

    // show (Rooms::OpensController and ClosedsController): `redirect_to room_url(@room)`.
    internal void RedirectToRoom() => RedirectTo(Routes.RoomUrl(UrlBase, CurrentRoom.Id));

    // destroy: `@room.destroy`, `broadcast_remove_room`, then back to the root. Without
    // `set_room` (Rooms::OpensController and ClosedsController don't run it for destroy) there is
    // no room, and Rails fails on nil.
    internal async ValueTask DestroyAsync()
    {
        var destroyed = room ?? throw new InvalidOperationException("undefined method 'destroy' for nil");
        await DestroyRoomAsync(destroyed).ConfigureAwait(false);

        // broadcast_remove_room: `broadcast_remove_to :rooms, target: [ @room, :list ]`
        TurboBroadcasts.Remove(App.RequireSeams().Broadcaster, ["rooms"], ListDomId(destroyed));
        RedirectTo(Routes.RootUrl(UrlBase));
    }

    // `room.destroy`: its memberships deleted without callbacks (`dependent: :delete_all`), each
    // message destroyed with its callbacks (`dependent: :destroy`), then the room, in one
    // transaction.
    async ValueTask DestroyRoomAsync(Room destroyed)
    {
        var now = Now;
        var seams = App.RequireSeams();
        await WriteAsync(transaction =>
        {
            var session = transaction.Session;
            Memberships.DeleteForRoom(session, destroyed.Id);
            foreach (var message in Messages.InRoom(session, destroyed.Id))
            {
                MessageAttachmentLifecycle.DestroyWithAttachment(transaction, seams, message, now);
            }
            Rooms.Delete(session, destroyed.Id);
        }).ConfigureAwait(false);
    }

    // `Current.user.can_administer?(room)`: administrators, the room's creator, and anyone with a
    // room not yet saved.
    private protected bool CanAdminister(Room? subject) => subject is null || User.CanAdminister(subject.CreatorId);

    /// <summary>The users of a room's access list (<c>User.active.ordered</c>, or a part of it).</summary>
    private protected List<RoomMember> Members(IEnumerable<User> users) =>
        [.. users.Select(user => new RoomMember(user.Id, user.Name,
            new AvatarUser(user.Id, user.Title, TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id), user.UpdatedAt)))];

    // `last_room_visited`'s id, for `link_back_to_last_room_visited`.
    private protected async ValueTask<long?> LastRoomIdAsync() => (await LastRoomVisitedAsync().ConfigureAwait(false))?.Id;

    // `dom_id(room, :list)`: the room's id in the sidebar.
    private protected static string ListDomId(Room subject) => RecordIdentifier.DomId(new RecordKey(subject.Type.ClassName(), subject.Id), "list");

    // `[user, :rooms]`: a user's own sidebar stream.
    private protected static string[] UserRooms(long userId) => [RecordIdentifier.GidParam(User.ModelName, userId), "rooms"];

    // `broadcast_prepend_to`
    private protected void BroadcastPrepend(IEnumerable<string> streamables, string target, string html) =>
        App.RequireSeams().Broadcaster.Broadcast(TurboBroadcasts.StreamName(streamables),
            RailsJson.Encode(JsonValue.Create(TurboBroadcasts.ActionTag("prepend", target, html))));

    // `users/sidebars/rooms/_shared` for `room`.
    private protected string RenderSharedRoom(SqliteSession session, Room subject)
    {
        var view = NewView(session, forgeryProtection: false);
        return RenderString(w => view.RoomsSidebarShared(w, subject));
    }

    // `users/sidebars/rooms/_direct` for one membership of a direct room.
    private protected string RenderDirectRoom(SqliteSession session, Room subject, Membership membership)
    {
        // `membership.room.users.without(membership.user).presence || [ membership.user ]`
        var others = Users.InRoom(session, subject.Id).Where(user => user.Id != membership.UserId).ToList();
        List<User> members = others.Count > 0 ? others : [Users.Find(session, membership.UserId) ?? throw new InvalidOperationException("The membership's user is gone")];
        var direct = new SidebarDirectRoom(subject.Id, subject.UpdatedAt, membership.IsUnread,
            [.. members.Select(member => (member.Name, Routes.FreshUserAvatarPath(TransferableUser.GenerateAvatarSignedId(App.Keys, member.Id), member.UpdatedAt)))]);
        var view = NewView(session, forgeryProtection: false);
        return RenderString(w => view.RoomsSidebarDirect(w, direct));
    }

    // The implicit render of an HTML page: turbo-rails' frame layout for a Turbo frame request,
    // else the application layout. The page is rendered first, so its content_for calls are in
    // place for the layout.
    private protected async ValueTask RenderPageAsync(Action<View, HtmlWriter> template)
    {
        RespondTo(MimeType.Html);
        var page = await ReadAsync(session =>
        {
            var view = NewView(session);
            var body = RenderString(w => template(view, w));
            var buffer = new ArrayBufferWriter<byte>();
            if (Request.IsTurboFrameRequest)
            {
                view.TurboRailsFrameLayout(new HtmlWriter(buffer), new SafeString(body));
            }
            else
            {
                view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(body));
            }
            return buffer.WrittenMemory;
        }).ConfigureAwait(false);
        Render(page, MimeType.Html.Value);
    }

    // The view a page renders in: the request, the current user and account, and the app's
    // assets. Broadcasts render without a session, so without forgery protection.
    View NewView(SqliteSession session, bool forgeryProtection = true)
    {
        var account = Accounts.First(session);
        var user = User;
        return new View
        {
            Assets = App.RequireAssets(),
            Origin = UrlBase,
            RequestPath = Request.Path,
            RequestUrl = RequestUrl.Url,
            Referrer = Referer,
            FormAuthenticityToken = forgeryProtection ? (action, method) => FormAuthenticityToken(action, method) : null,
            StreamKeys = App.Keys,
            Flash = Flash,
            CurrentUser = new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            RichTextContext = new RenderContext(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host),
            Storage = App.RequireStorage(),
        };
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// rooms#destroy (<c>RoomsController#destroy</c>): any of the user's rooms, by those who can
/// administer it.
/// </summary>
public sealed class RoomsDestroyController : RoomSettingsController
{
    static readonly ControllerCallbacks<RoomsDestroyController> Chain = RoomsCallbacks<RoomsDestroyController>();

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());
}

/// <summary>The params the room settings read.</summary>
static class RoomSettingsParams
{
    static readonly PermitFilter[] RoomParams = [PermitFilter.Key("name")];

    /// <summary>
    /// <c>room_params</c>: <c>params.require(:room).permit(:name)</c>, as the name to assign, or
    /// null when it holds none (the name is left as it is).
    /// </summary>
    public static Change<string?>? Name(ParamHash parameters) =>
        parameters.RequireHash("room").Permit(RoomParams).TryGetValue("name", out var name)
            ? new Change<string?>(name is null ? null : RubyValues.ToS(name))
            : null;

    /// <summary>
    /// <c>params.fetch(:user_ids, [])</c> as <c>User.where(id:)</c> casts them: each to an
    /// integer, those that aren't one dropped (they match no user).
    /// </summary>
    public static List<long> UserIds(ParamHash parameters) => parameters.Fetch("user_ids", null) switch
    {
        List<object?> ids => [.. ids.Select(Cast).OfType<long>()],
        null => [],
        var id => [.. new[] { Cast(id) }.OfType<long>()],
    };

    static long? Cast(object? id) => id is string text ? ActiveModelInteger.Cast(text) : id as long?;
}
