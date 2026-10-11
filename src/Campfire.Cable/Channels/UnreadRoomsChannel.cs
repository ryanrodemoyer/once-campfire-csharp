using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Formatting;

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
    /// <c>Message::Broadcasts#broadcast_unread_room</c> (reference/app/models/message/broadcasts.rb),
    /// which <c>Message::BroadcastUnreadRoomJob</c> runs: <c>{"roomId": id, "at": created_at.to_f}</c>
    /// encoded once, then <c>coder: nil</c> to each member who still has the room unread or is in
    /// it now (<c>room.memberships.unread.or(room.memberships.connected).pluck(:user_id)</c>), in
    /// the order the query returns them. By the time the job runs, someone may have opened the room
    /// and moved on, and would see it marked unread again.
    /// </summary>
    public static void BroadcastUnread(IBroadcaster broadcaster, SqliteSession session, Message message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        var payload = RailsJson.Encode(new JsonObject { ["roomId"] = message.RoomId, ["at"] = TimeFormats.ToF(message.CreatedAt) });
        foreach (var userId in Memberships.UnreadOrConnectedUserIds(session, message.RoomId, now))
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
