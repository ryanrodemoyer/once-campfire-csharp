using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>PresenceChannel</c> (reference/app/channels/presence_channel.rb): a <c>RoomChannel</c> that
/// marks the membership connected while subscribed (<c>Membership::Connectable</c>) and tells the
/// user's other windows the room has been read.
/// </summary>
public sealed class PresenceChannel : Channel<User>
{
    readonly SqliteDatabase database;
    Room? room;

    public PresenceChannel(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary><c>subscribed</c>, then <c>on_subscribe :present, unless: :subscription_rejected?</c>.</summary>
    public override async ValueTask SubscribedAsync()
    {
        room = await RoomSubscription.OpenAsync(database, this).ConfigureAwait(false);
        if (!SubscriptionRejected)
        {
            await PresentAsync().ConfigureAwait(false);
        }
    }

    /// <summary><c>on_unsubscribe :absent, unless: :subscription_rejected?</c>.</summary>
    public override async ValueTask UnsubscribedAsync()
    {
        if (!SubscriptionRejected)
        {
            await AbsentAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Public actions: <c>present</c>, <c>absent</c>, <c>refresh</c>, and <c>subscribed</c> inherited
    /// from <c>RoomChannel</c>. Performing <c>subscribed</c> does not run <c>on_subscribe</c>.
    /// </summary>
    public override async ValueTask<bool> PerformAsync(string action, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (SubscriptionRejected)
        {
            return false;
        }

        switch (action)
        {
            case "present":
                await PresentAsync().ConfigureAwait(false);
                return true;
            case "absent":
                await AbsentAsync().ConfigureAwait(false);
                return true;
            case "refresh":
                await RefreshAsync().ConfigureAwait(false);
                return true;
            case "subscribed":
                room = await RoomSubscription.OpenAsync(database, this).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary><c>present</c>: <c>membership.present</c>, then <c>broadcast_read_room</c>.</summary>
    async ValueTask PresentAsync()
    {
        await WithMembershipAsync(Memberships.Present).ConfigureAwait(false);
        // `membership.room_id` finds the membership again.
        var current = await MembershipAsync().ConfigureAwait(false);
        ReadRoomsChannel.BroadcastRead(Server, CurrentUser.Id, current.RoomId);
    }

    /// <summary><c>absent</c>: <c>membership.disconnected</c>.</summary>
    ValueTask AbsentAsync() => WithMembershipAsync(Memberships.Disconnected);

    /// <summary><c>refresh</c>: <c>membership.refresh_connection</c>.</summary>
    ValueTask RefreshAsync() => WithMembershipAsync(Memberships.RefreshConnection);

    async ValueTask WithMembershipAsync(Func<SqliteSession, Membership, DateTimeOffset, Membership> change)
    {
        var (roomId, userId) = Ids();
        var now = Server.Clock.GetUtcNow();
        var found = await database.WriteAsync(tx =>
        {
            var membership = Memberships.FindFor(tx.Session, userId, roomId);
            if (membership is null)
            {
                return false;
            }

            change(tx.Session, membership, now);
            return true;
        }).ConfigureAwait(false);
        if (!found)
        {
            throw new InvalidOperationException("undefined method for nil (membership)");
        }
    }

    async ValueTask<Membership> MembershipAsync()
    {
        var (roomId, userId) = Ids();
        var membership = await database.ReadAsync(session => Memberships.FindFor(session, userId, roomId)).ConfigureAwait(false);
        return membership ?? throw new InvalidOperationException("undefined method for nil (membership)");
    }

    (long RoomId, long UserId) Ids()
    {
        // `@room` is only nil after a rejection, when these callbacks don't run.
        var current = room ?? throw new InvalidOperationException("undefined method 'memberships' for nil");
        return (current.Id, CurrentUser.Id);
    }
}
