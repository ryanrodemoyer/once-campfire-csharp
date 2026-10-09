using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Data.Events;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>UnreadRoomsChannel</c> (reference/app/channels/unread_rooms_channel.rb): per user, so activity
/// in a room reaches only its members. Stream <c>user_&lt;id&gt;_unreads</c>.
/// </summary>
public sealed class UnreadRoomsChannel : Channel<User>
{
    /// <summary><c>UnreadRoomsChannel.stream_name_for</c>.</summary>
    public static string StreamNameFor(long userId) => $"user_{userId}_unreads";

    /// <summary>
    /// <c>Message::Broadcasts#broadcast_unread_room</c> (reference/app/models/message/broadcasts.rb):
    /// <c>{"roomId": id}</c> encoded once, then <c>coder: nil</c> to each
    /// <c>room.memberships.pluck(:user_id)</c> in the order the query returns them.
    /// </summary>
    public static void BroadcastUnread(IBroadcaster broadcaster, SqliteSession session, long roomId)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(session);
        var payload = RailsJson.Encode(new JsonObject { ["roomId"] = roomId });
        var userIds = session.Query(
            """SELECT "memberships"."user_id" FROM "memberships" WHERE "memberships"."room_id" = @room_id""",
            reader => reader.GetInt64(0),
            ("@room_id", roomId));
        foreach (var userId in userIds)
        {
            broadcaster.Broadcast(StreamNameFor(userId), payload);
        }
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
