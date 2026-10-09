using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Data.Records;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>ReadRoomsChannel</c> (reference/app/channels/read_rooms_channel.rb): the user's own stream of
/// rooms read in another window, <c>user_&lt;id&gt;_reads</c>.
/// </summary>
public sealed class ReadRoomsChannel : Channel<User>
{
    /// <summary><c>"user_#{user_id}_reads"</c>.</summary>
    public static string StreamNameFor(long userId) => $"user_{userId}_reads";

    /// <summary>
    /// <c>PresenceChannel#broadcast_read_room</c>: <c>{ room_id: }</c> on that user's reads stream.
    /// </summary>
    public static int BroadcastRead(CableServer<User> server, long userId, long roomId)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Broadcast(StreamNameFor(userId), new JsonObject { ["room_id"] = roomId });
    }

    /// <summary><c>subscribed</c>.</summary>
    public override ValueTask SubscribedAsync()
    {
        StreamFrom(StreamNameFor(CurrentUser.Id));
        return ValueTask.CompletedTask;
    }

    /// <summary><c>subscribed</c> is public on the subclass, so a client can perform it too.</summary>
    public override ValueTask<bool> PerformAsync(string action, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (action != "subscribed")
        {
            return ValueTask.FromResult(false);
        }

        StreamFrom(StreamNameFor(CurrentUser.Id));
        return ValueTask.FromResult(true);
    }
}
