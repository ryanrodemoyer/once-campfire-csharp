using System.Buffers;
using System.Text;
using Campfire.Data.Pagination;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Ruby;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>RoomsController</c> (reference/app/controllers/rooms_controller.rb): <c>index</c> redirects
/// to the user's newest room; <c>show</c> renders a room's last page of messages, or the page
/// around <c>/rooms/:room_id/@:message_id</c>, and remembers the room as the last one visited.
/// </summary>
public sealed class RoomsController : ApplicationController
{
    static readonly ControllerCallbacks<RoomsController> Chain = Callbacks.For<RoomsController>()
        .Before("set_room", c => c.SetRoomAsync(), only: ["show", "destroy"])
        .Before("ensure_can_administer", c => c.EnsureCanAdministerRoom(), only: ["destroy"])
        .Before("remember_last_room_visited", c => c.RememberLastRoomVisited(c.CurrentRoom.Id), only: ["show"]);

    Room? room;

    /// <summary><c>@room</c>, which <c>set_room</c> found.</summary>
    Room CurrentRoom => room ?? throw new InvalidOperationException("set_room didn't run");

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");

    /// <summary><c>redirect_to room_url(Current.user.rooms.last)</c>.</summary>
    public static readonly RequestDelegate Index = Action(Chain, async c =>
    {
        var userId = c.User.Id;
        // `rooms.last` without an order is the highest primary key; `room_url(nil)` raises.
        var last = (await c.ReadAsync(session => Rooms.ForUser(session, userId))).MaxBy(room => room.Id)
            ?? throw new InvalidOperationException("No route matches room_url(nil): missing required keys: [:id]");
        c.RedirectTo(Routes.RoomUrl(c.UrlBase, last.Id));
    });

    /// <summary><c>@messages = find_messages</c>, then <c>rooms/show</c> in the application layout.</summary>
    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    async ValueTask ShowAsync()
    {
        RespondTo(MimeType.Html);
        var html = await ReadAsync(session =>
        {
            var view = NewView(session);
            var page = new RoomPage(
                CurrentRoom,
                RoomDisplayName(session),
                LoadMessages(session, FindMessages(session)),
                MessageViewsFor(session).User(User),
                ShowsInvitation(session),
                Accounts.First(session)?.JoinCode,
                Platform);
            var body = RenderString(w => view.RoomsShow(w, page));
            return RenderLayout(view, body);
        }).ConfigureAwait(false);
        Render(html, MimeType.Html.Value);
    }

    // set_room: `room_scope.find_by(id: params[:room_id] || params[:id])`, else back to the root.
    async ValueTask SetRoomAsync()
    {
        var userId = User.Id;
        var id = (Params["room_id"] ?? Params["id"]) is string param ? ActiveModelInteger.Cast(param) : null;
        room = id is { } roomId ? await ReadAsync(session => Rooms.FindForUser(session, userId, roomId)).ConfigureAwait(false) : null;
        if (room is null)
        {
            RedirectTo(Routes.RootUrl(UrlBase), alert: "Room not found or inaccessible");
        }
    }

    // ensure_can_administer: `head :forbidden unless Current.user.can_administer?(@room)`.
    void EnsureCanAdministerRoom()
    {
        if (!User.CanAdminister(CurrentRoom.CreatorId))
        {
            Head(403);
        }
    }

    // find_messages: the page around `params[:message_id]` when it names one of the room's
    // messages, else the last page.
    List<Message> FindMessages(SqliteSession session)
    {
        var roomId = CurrentRoom.Id;
        if (Params["message_id"] is string param && RubyValues.IsPresent(param)
            && ActiveModelInteger.Cast(param) is { } messageId
            && Messages.FindInRoom(session, roomId, messageId) is { } showFirstMessage)
        {
            return MessagePages.PageAround(session, roomId, showFirstMessage);
        }
        return MessagePages.LastPage(session, roomId);
    }

    // rooms/show/_invitation: `@room == Room.original && !@room.messages.paged?`.
    bool ShowsInvitation(SqliteSession session) =>
        Rooms.Original(session)?.Id == CurrentRoom.Id && !MessagePages.IsPaged(session, CurrentRoom.Id);

    // room_display_name(@room) for Current.user.
    string? RoomDisplayName(SqliteSession session)
    {
        var members = CurrentRoom.IsDirect ? Users.InRoom(session, CurrentRoom.Id).Select(user => (user.Id, user.Name)) : [];
        return View.RoomDisplayName(CurrentRoom.Name, CurrentRoom.IsDirect, members, User.Id, User.Name);
    }

    List<MessageView> LoadMessages(SqliteSession session, List<Message> messages) => MessageViewsFor(session).Load(session, messages);

    MessageViews MessageViewsFor(SqliteSession session) =>
        new(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session)));

    // The view a page renders in: the request, the current user and account, and the app's assets.
    View NewView(SqliteSession session)
    {
        var account = Accounts.First(session);
        return new View
        {
            Assets = App.RequireAssets(),
            Origin = UrlBase,
            RequestPath = Request.Path,
            RequestUrl = RequestUrl.Url,
            Referrer = Referer,
            FormAuthenticityToken = (action, method) => FormAuthenticityToken(action, method),
            StreamKeys = App.Keys,
            Flash = Flash,
            CurrentUser = new CurrentUser(User.Id, User.Name, User.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            RichTextContext = RichTextContext(session),
            Storage = App.RequireStorage(),
        };
    }

    RenderContext RichTextContext(SqliteSession session) => new(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host);

    // The page is rendered first, so its content_for calls are in place for the layout.
    static ReadOnlyMemory<byte> RenderLayout(View view, string page)
    {
        var buffer = new ArrayBufferWriter<byte>();
        view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(page));
        return buffer.WrittenMemory;
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
