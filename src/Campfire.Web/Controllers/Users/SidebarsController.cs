using System.Text;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Users::SidebarsController</c> (reference/app/controllers/users/sidebars_controller.rb):
/// renders the sidebar with direct and shared rooms, unread state, placeholders and tools.
/// </summary>
public sealed class UsersSidebarsController : ApplicationController
{
    const int directPlaceholders = 20;

    static readonly ControllerCallbacks<UsersSidebarsController> Chain = Callbacks.For<UsersSidebarsController>();

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    async ValueTask ShowAsync()
    {
        var user = User;
        var sidebarPage = await ReadAsync(session => LoadSidebarPage(session, user)).ConfigureAwait(false);
        await RenderPageAsync((view, w) => view.UsersSidebarsShow(w, sidebarPage)).ConfigureAwait(false);
    }

    SidebarPage LoadSidebarPage(SqliteSession session, User user)
    {
        var memberships = Memberships.ForUserWithOrderedRoom(session, user.Id, visibleOnly: true);

        var directPairs = new List<(Membership Membership, Room Room)>();
        var otherPairs = new List<(Membership Membership, Room Room)>();
        foreach (var pair in memberships)
        {
            if (pair.Room.Type == RoomType.Direct)
            {
                directPairs.Add(pair);
            }
            else
            {
                otherPairs.Add(pair);
            }
        }

        var sortedDirect = directPairs.OrderBy(p => p.Room.UpdatedAt).Reverse().ToList();

        var directRooms = new List<SidebarDirectRoom>();
        foreach (var pair in sortedDirect)
        {
            var roomUsers = Users.InRoom(session, pair.Room.Id);
            var others = roomUsers.Where(u => u.Id != user.Id).ToList();
            var members = others.Count > 0 ? others : (roomUsers.Count > 0 ? roomUsers : [user]);
            var memberItems = members.Select(m => (m.Name, Routes.FreshUserAvatarPath(TransferableUser.GenerateAvatarSignedId(App.Keys, m.Id), m.UpdatedAt))).ToList();
            directRooms.Add(new SidebarDirectRoom(pair.Room.Id, pair.Room.UpdatedAt, pair.Membership.UnreadAt is not null, memberItems));
        }

        var allUserDirectRoomIds = Rooms.IdsForUserOfType(session, user.Id, RoomType.Direct);
        var userIdsInDirectRooms = Memberships.UserIdsInRooms(session, allUserDirectRoomIds);
        var excludeUserIds = new List<long>(userIdsInDirectRooms) { user.Id };
        var limit = Math.Max(directPlaceholders - excludeUserIds.Count, 0);

        var placeholderUsers = new List<SidebarPlaceholderUser>();
        if (limit > 0)
        {
            var activeUsers = Users.ActiveExcludingByCreation(session, excludeUserIds.Distinct().ToList(), limit);
            placeholderUsers = activeUsers.Select(u => new SidebarPlaceholderUser(u.Id, u.Name, Routes.FreshUserAvatarPath(TransferableUser.GenerateAvatarSignedId(App.Keys, u.Id), u.UpdatedAt))).ToList();
        }

        var otherRooms = otherPairs.Select(p => (p.Room, p.Membership.UnreadAt is not null)).ToList();

        var account = Accounts.First(session);
        var canCreateRoom = user.CanAdminister() || !AccountSettings.Parse(account?.Settings).RestrictRoomCreationToAdministrators;
        var userAvatarPath = Routes.FreshUserAvatarPath(TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id), user.UpdatedAt);

        return new SidebarPage(
            new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            directRooms,
            otherRooms,
            placeholderUsers,
            canCreateRoom,
            userAvatarPath);
    }

    async ValueTask RenderPageAsync(Action<View, HtmlWriter> template)
    {
        RespondTo(MimeType.Html);
        var page = await ReadAsync(session =>
        {
            var view = NewView(session);
            var body = RenderString(w => template(view, w));
            using var buffer = new PooledBufferWriter();
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

    View NewView(SqliteSession session)
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
            FormAuthenticityToken = (action, method) => FormAuthenticityToken(action, method),
            StreamKeys = App.Keys,
            Flash = Flash,
            CurrentUser = new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            Storage = App.RequireStorage(),
        };
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        using var buffer = new PooledBufferWriter();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
