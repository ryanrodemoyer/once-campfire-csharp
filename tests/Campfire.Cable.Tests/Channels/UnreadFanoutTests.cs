using System.Globalization;
using Campfire.Cable.Channels;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Cable.Tests.Channels;

/// <summary>
/// <c>broadcast_unread_room</c> (reference/app/models/message/broadcasts.rb): one
/// <c>{"roomId": id}</c> per member, unencoded, in pluck order.
/// </summary>
public sealed class UnreadFanoutTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-cable-fanout", Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;

    public UnreadFanoutTests()
    {
        Directory.CreateDirectory(directory);
        database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(directory, "production.sqlite3")) { Readers = 1 });
    }

    public void Dispose()
    {
        database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Unread_fanout_to_1000_members_matches_the_reference_payload()
    {
        var now = ChannelHarness.Start;
        var (room, outsiderId) = await database.WriteAsync(tx =>
        {
            var ids = new List<long>(1000);
            for (var i = 0; i < 1000; i++)
            {
                ids.Add(Users.Create(tx.Session, $"Member {i.ToString(CultureInfo.InvariantCulture)}", $"member-{i.ToString(CultureInfo.InvariantCulture)}@example.com", null, now).Id);
            }

            var outsider = Users.Create(tx.Session, "Outsider", "outsider@example.com", null, now);
            return (Rooms.CreateFor(tx.Session, RoomType.Open, "Thousand", ids[0], ids, now), outsider.Id);
        }, TestContext.Current.CancellationToken);

        var recorded = new RecordingSeams();
        var plucked = await database.ReadAsync(session =>
        {
            UnreadRoomsChannel.BroadcastUnread(recorded, session, room.Id);
            return session.Query(
                """SELECT "memberships"."user_id" FROM "memberships" WHERE "memberships"."room_id" = @room_id""",
                reader => reader.GetInt64(0),
                ("@room_id", room.Id));
        }, TestContext.Current.CancellationToken);

        var payload = string.Create(CultureInfo.InvariantCulture, $"{{\"roomId\":{room.Id}}}");
        Assert.Equal(1000, recorded.Broadcasts.Count);
        Assert.Equal(1000, plucked.Count);
        Assert.Equal(plucked.Select(UnreadRoomsChannel.StreamNameFor), recorded.Broadcasts.Select(broadcast => broadcast.Stream));
        Assert.All(recorded.Broadcasts, broadcast => Assert.Equal(payload, broadcast.Payload));
        Assert.DoesNotContain(recorded.Broadcasts, broadcast => broadcast.Stream == UnreadRoomsChannel.StreamNameFor(outsiderId));
    }
}
