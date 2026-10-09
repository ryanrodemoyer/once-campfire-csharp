using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>RoomChannel</c> (reference/app/channels/room_channel.rb).
/// </summary>
public sealed class RoomChannel : Channel<User>
{
    readonly SqliteDatabase database;
    Room? room;

    public RoomChannel(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary>The room this subscription is streaming, when <c>subscribed</c> found one.</summary>
    public Room? Room => room;

    /// <summary><c>subscribed</c>.</summary>
    public override async ValueTask SubscribedAsync() => room = await RoomSubscription.OpenAsync(database, this).ConfigureAwait(false);

    /// <summary><c>subscribed</c> is public, so it's an action. <c>on_subscribe</c> callbacks do not run.</summary>
    public override async ValueTask<bool> PerformAsync(string action, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (SubscriptionRejected || action != "subscribed")
        {
            return false;
        }

        await SubscribedAsync().ConfigureAwait(false);
        return true;
    }
}
