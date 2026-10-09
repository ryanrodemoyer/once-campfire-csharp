using Campfire.Cable.Server;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.GlobalId;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>RoomChannel#subscribed</c> (reference/app/channels/room_channel.rb): <c>stream_for @room</c>
/// when <c>current_user.rooms.find_by(id: params[:room_id])</c> finds one, otherwise <c>reject</c>.
/// <c>PresenceChannel</c> and <c>TypingNotificationsChannel</c> inherit this lookup.
/// </summary>
public static class RoomSubscription
{
    /// <summary>The room the subscription is streaming, or null when it was rejected.</summary>
    public static async ValueTask<Room?> OpenAsync(SqliteDatabase database, Channel<User> channel)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(channel);
        var roomId = RoomIds.Cast(channel.Params["room_id"]);
        Room? room = null;
        if (roomId is { } id)
        {
            var userId = channel.CurrentUser.Id;
            room = await database.ReadAsync(session => Rooms.FindForUser(session, userId, id)).ConfigureAwait(false);
        }

        if (room is null)
        {
            channel.Reject();
        }
        else
        {
            channel.StreamFor(GidParam(room));
        }

        return room;
    }

    /// <summary>A room's GlobalID param names its STI class (<c>gid://campfire/Rooms::Open/1</c>).</summary>
    public static string GidParam(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return GlobalId.Create(room.Type.ClassName(), room.Id).ToParam();
    }
}
