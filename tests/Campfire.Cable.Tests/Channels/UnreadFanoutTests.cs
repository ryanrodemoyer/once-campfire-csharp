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
/// <c>{"roomId": id, "at": created_at.to_f}</c>, unencoded, to each member who still has the room
/// unread or is in it now, in pluck order.
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
    public async Task Unread_fanout_to_1000_members_goes_to_the_unread_and_connected_ones_with_the_reference_payload()
    {
        var now = ChannelHarness.Start;
        var (message, outsiderId) = await database.WriteAsync(tx =>
        {
            var ids = new List<long>(1000);
            for (var i = 0; i < 1000; i++)
            {
                ids.Add(Users.Create(tx.Session, $"Member {i.ToString(CultureInfo.InvariantCulture)}", $"member-{i.ToString(CultureInfo.InvariantCulture)}@example.com", null, now).Id);
            }

            var outsider = Users.Create(tx.Session, "Outsider", "outsider@example.com", null, now);
            var room = Rooms.CreateFor(tx.Session, RoomType.Open, "Thousand", ids[0], ids, now);
            // A quarter unread, a quarter in the room now, a quarter who left it two minutes ago and
            // a quarter who read it and moved on.
            for (var i = 0; i < 1000; i++)
            {
                var (unreadAt, connectedAt) = (i % 4) switch
                {
                    0 => ("2026-03-02 15:00:00", (string?)null),
                    1 => (null, "2026-03-02 16:00:00"),
                    2 => (null, "2026-03-02 15:58:00"),
                    _ => ((string?)null, (string?)null),
                };
                tx.Session.Execute("""UPDATE "memberships" SET "unread_at" = @unread_at, "connected_at" = @connected_at WHERE "room_id" = @room_id AND "user_id" = @user_id""",
                    ("@unread_at", unreadAt), ("@connected_at", connectedAt), ("@room_id", room.Id), ("@user_id", ids[i]));
            }
            return (Messages.Create(tx.Session, room.Id, ids[1], "fanout", now.AddMilliseconds(500)), outsider.Id);
        }, TestContext.Current.CancellationToken);

        var recorded = new RecordingSeams();
        var plucked = await database.ReadAsync(session =>
        {
            UnreadRoomsChannel.BroadcastUnread(recorded, session, message, now);
            return session.Query(
                """SELECT "memberships"."user_id" FROM "memberships" WHERE "memberships"."room_id" = @room_id AND ("memberships"."unread_at" IS NOT NULL OR "memberships"."connected_at" >= @cutoff)""",
                reader => reader.GetInt64(0),
                ("@room_id", message.RoomId), ("@cutoff", "2026-03-02 15:59:00"));
        }, TestContext.Current.CancellationToken);

        var payload = string.Create(CultureInfo.InvariantCulture, $"{{\"roomId\":{message.RoomId},\"at\":1772467200.5}}");
        Assert.Equal(500, recorded.Broadcasts.Count);
        Assert.Equal(plucked.Select(UnreadRoomsChannel.StreamNameFor), recorded.Broadcasts.Select(broadcast => broadcast.Stream));
        Assert.All(recorded.Broadcasts, broadcast => Assert.Equal(payload, broadcast.Payload));
        Assert.DoesNotContain(recorded.Broadcasts, broadcast => broadcast.Stream == UnreadRoomsChannel.StreamNameFor(outsiderId));
    }
}
