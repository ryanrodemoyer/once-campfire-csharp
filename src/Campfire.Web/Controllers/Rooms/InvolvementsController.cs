using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Formatting;
using Campfire.Web.Broadcasts;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Rooms::InvolvementsController</c> (reference/app/controllers/rooms/involvements_controller.rb):
/// the notification bell's button for the user's involvement in a room, and changing it. A shared
/// room the user makes invisible leaves their sidebar, and comes back when they make it visible.
/// It isn't a RoomsController in Rails (its room comes from the user's membership, RoomScoped);
/// it shares the room settings' page rendering and sidebar broadcasts.
/// </summary>
public sealed class RoomsInvolvementsController : RoomSettingsController
{
    static readonly ControllerCallbacks<RoomsInvolvementsController> Chain = Callbacks.For<RoomsInvolvementsController>()
        .Before("set_room", c => c.SetMembershipAsync());

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    Membership? membership;

    Membership CurrentMembership => membership ?? throw new InvalidOperationException("set_room hasn't run");

    // show: `@involvement = @membership.involvement`. Without one, the bell's image is
    // `notification-bell-.svg`, which isn't an asset, and the page fails as in Rails.
    ValueTask ShowAsync()
    {
        var (current, involvement) = (CurrentRoom, CurrentMembership.Involvement?.Name() ?? "");
        return RenderPageAsync((view, w) => view.RoomsInvolvementsShow(w, current, involvement));
    }

    // update: `@membership.update! involvement: params[:involvement]`, then the sidebar follows a
    // change of visibility.
    async ValueTask UpdateAsync()
    {
        var involvement = Involvement(Params["involvement"]);
        var (previous, now) = (CurrentMembership, Now);
        var updated = await WriteAsync(transaction => UpdateInvolvement(transaction.Session, previous, involvement, now)).ConfigureAwait(false);

        await BroadcastVisibilityChangesAsync(previous, updated).ConfigureAwait(false);
        RedirectTo(Routes.RoomInvolvementUrl(UrlBase, CurrentRoom.Id));
    }

    // broadcast_visibility_changes
    async ValueTask BroadcastVisibilityChangesAsync(Membership previous, Membership updated)
    {
        var current = CurrentRoom;
        if (current.IsDirect)
        {
            return;
        }
        if (updated.Involvement == Data.Records.Involvement.Invisible)
        {
            TurboBroadcasts.Remove(App.RequireSeams().Broadcaster, UserRooms(updated.UserId), ListDomId(current));
        }
        // `involvement_previously_was.inquiry.invisible?`: nil has no `inquiry`.
        else if ((previous.Involvement ?? throw new InvalidOperationException("undefined method 'inquiry' for nil")) == Data.Records.Involvement.Invisible)
        {
            var html = await ReadAsync(session => RenderSharedRoom(session, current)).ConfigureAwait(false);
            BroadcastPrepend(UserRooms(updated.UserId), "shared_rooms", html);
        }
    }

    // RoomScoped's set_room: `Current.user.memberships.find_by!(room_id: params[:room_id])` and its room.
    async ValueTask SetMembershipAsync() => (membership, room) = await RoomScoped.FindRoomAsync(this).ConfigureAwait(false);

    // The enum's assignment: a name it has, or nil for nil; anything else is an ArgumentError.
    static Change<Involvement?> Involvement(object? param) => param switch
    {
        null => new(null),
        string name when Involvements.FromName(name) is { } value => new(value),
        _ => throw new ArgumentException($"'{param}' is not a valid involvement"),
    };

    // `update!(involvement:)`, writing nothing when it doesn't change. Setting it to nil isn't
    // something Campfire.Data's memberships offer, so it is written here.
    static Membership UpdateInvolvement(SqliteSession session, Membership stored, Change<Involvement?> involvement, DateTimeOffset now)
    {
        if (involvement.Value is { } value)
        {
            return Memberships.UpdateInvolvement(session, stored, value, now);
        }
        if (stored.Involvement is null)
        {
            return stored;
        }
        session.Execute("UPDATE \"memberships\" SET \"involvement\" = @involvement, \"updated_at\" = @updated_at WHERE \"memberships\".\"id\" = @id",
            ("@involvement", null), ("@updated_at", ActiveRecordTime.ToDb(now)), ("@id", stored.Id));
        return Memberships.Find(session, stored.Id)!;
    }
}
