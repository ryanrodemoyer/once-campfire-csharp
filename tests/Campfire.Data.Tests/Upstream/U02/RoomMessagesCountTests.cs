using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests.Upstream.U02;

// Port of reference/test/models/room/messages_count_test.rb
public sealed class RoomMessagesCountTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt;
    static readonly string[] FixtureRoomLabels = ["watercooler", "designers", "bender_and_kevin"];

    readonly Fixtures.Database fixtures = new();

    public void Dispose() => fixtures.Dispose();

    static long User(string label) => Fixtures.Id(label);

    static Room GetRoom(SqliteSession session, string label) => Rooms.Find(session, Fixtures.Id(label))!;

    [Fact]
    public void Fixture_rooms_start_with_an_accurate_messages_count()
    {
        fixtures.Write(session =>
        {
            foreach (var label in FixtureRoomLabels)
            {
                var room = GetRoom(session, label);
                var realCount = Messages.CountInRoom(session, room.Id);
                Assert.Equal(realCount, room.MessagesCount);
            }
            return 0;
        });
    }

    [Fact]
    public void Create_and_destroy_adjust_the_counter_once()
    {
        fixtures.Write(session =>
        {
            var room = GetRoom(session, "designers");
            var beforeCount = room.MessagesCount;
            var beforeReal = Messages.CountInRoom(session, room.Id);
            Assert.Equal(beforeReal, beforeCount);

            var message = Messages.Create(session, room.Id, User("jason"), "count-ar-create", Now);

            var afterCreateRoom = GetRoom(session, "designers");
            Assert.Equal(beforeCount + 1, afterCreateRoom.MessagesCount);
            Assert.Equal(Messages.CountInRoom(session, room.Id), afterCreateRoom.MessagesCount);

            Messages.Delete(session, message.Id);

            var afterDeleteRoom = GetRoom(session, "designers");
            Assert.Equal(beforeCount, afterDeleteRoom.MessagesCount);
            Assert.Equal(Messages.CountInRoom(session, room.Id), afterDeleteRoom.MessagesCount);
            return 0;
        });
    }

    [Fact]
    public void Bulk_insert_and_delete_keep_the_counter_in_step()
    {
        fixtures.Write(session =>
        {
            var room = GetRoom(session, "designers");
            var beforeCount = room.MessagesCount;

            for (var i = 0; i < 3; i++)
            {
                session.Execute("""
                    INSERT INTO "messages" ("room_id", "creator_id", "client_message_id", "created_at", "updated_at")
                    VALUES (@room_id, @creator_id, @client_message_id, @now, @now)
                    """,
                    ("@room_id", room.Id),
                    ("@creator_id", User("david")),
                    ("@client_message_id", $"count-bulk-{i}"),
                    ("@now", Db.Time(Now)));
            }

            var afterInsertRoom = GetRoom(session, "designers");
            Assert.Equal(beforeCount + 3, afterInsertRoom.MessagesCount);
            Assert.Equal(Messages.CountInRoom(session, room.Id), afterInsertRoom.MessagesCount);

            session.Execute("DELETE FROM messages WHERE client_message_id LIKE 'count-bulk-%'");

            var afterDeleteRoom = GetRoom(session, "designers");
            Assert.Equal(beforeCount, afterDeleteRoom.MessagesCount);
            Assert.Equal(Messages.CountInRoom(session, room.Id), afterDeleteRoom.MessagesCount);
            return 0;
        });
    }

    [Fact]
    public void Rolled_back_writes_leave_the_counter_unchanged()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);
        var room = GetRoom(session, "designers");
        var before = room.MessagesCount;

        try
        {
            WriteTransaction.Run<bool>(session, tx =>
            {
                Messages.Create(tx.Session, room.Id, User("jason"), "count-rollback", Now);
                throw new InvalidOperationException("Simulated rollback");
            });
        }
        catch (InvalidOperationException)
        {
        }

        var after = GetRoom(session, "designers");
        Assert.Equal(before, after.MessagesCount);
        Assert.Null(session.Scalar<long?>("SELECT id FROM messages WHERE client_message_id = 'count-rollback'"));
    }

    [Fact]
    public void Moving_a_message_between_rooms_moves_the_counter()
    {
        fixtures.Write(session =>
        {
            var designers = GetRoom(session, "designers");
            var pets = GetRoom(session, "pets");
            var designersBefore = designers.MessagesCount;
            var petsBefore = pets.MessagesCount;

            var message = Messages.Create(session, designers.Id, User("jason"), "count-move", Now);

            Assert.Equal(designersBefore + 1, GetRoom(session, "designers").MessagesCount);
            Assert.Equal(petsBefore, GetRoom(session, "pets").MessagesCount);

            session.Execute("UPDATE messages SET room_id = @new_room_id WHERE id = @id",
                ("@new_room_id", pets.Id), ("@id", message.Id));

            var designersAfter = GetRoom(session, "designers");
            var petsAfter = GetRoom(session, "pets");

            Assert.Equal(designersBefore, designersAfter.MessagesCount);
            Assert.Equal(petsBefore + 1, petsAfter.MessagesCount);

            Assert.Equal(Messages.CountInRoom(session, designers.Id), designersAfter.MessagesCount);
            Assert.Equal(Messages.CountInRoom(session, pets.Id), petsAfter.MessagesCount);
            return 0;
        });
    }
}
