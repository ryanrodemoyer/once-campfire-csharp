using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>TypingNotificationsChannel</c> (reference/app/channels/typing_notifications_channel.rb).
/// </summary>
public sealed class TypingNotificationsChannel : Channel<User>
{
    readonly SqliteDatabase database;
    Room? room;

    public TypingNotificationsChannel(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary><c>subscribed</c>, inherited from <c>RoomChannel</c>.</summary>
    public override async ValueTask SubscribedAsync() => room = await RoomSubscription.OpenAsync(database, this).ConfigureAwait(false);

    /// <summary><c>start</c>, <c>stop</c>, and the inherited public <c>subscribed</c>.</summary>
    public override async ValueTask<bool> PerformAsync(string action, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (SubscriptionRejected)
        {
            return false;
        }

        switch (action)
        {
            case "start":
                Broadcast("start");
                return true;
            case "stop":
                Broadcast("stop");
                return true;
            case "subscribed":
                room = await RoomSubscription.OpenAsync(database, this).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary><c>broadcast_to @room, action:, user: current_user.slice(:id, :name)</c>.</summary>
    void Broadcast(string action)
    {
        // `@room` is only nil when `subscribed` failed, and Rails raises NoMethodError.
        var current = room ?? throw new InvalidOperationException("undefined method 'to_gid_param' for nil");
        var payload = new JsonObject
        {
            ["action"] = action,
            ["user"] = new JsonObject { ["id"] = CurrentUser.Id, ["name"] = CurrentUser.Name },
        };
        BroadcastTo([RoomSubscription.GidParam(current)], payload);
    }
}
