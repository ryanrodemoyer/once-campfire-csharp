using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Schema;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests.Upstream.U02;

// Port of reference/test/models/room/messages_count_lifecycle_test.rb,
// messages_count_tasks_test.rb, and U02 acceptance criteria.
public sealed class RoomMessagesCountLifecycleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = Fixtures.LoadedAt;

    static long User(string label) => Fixtures.Id(label);

    static Room GetRoom(SqliteSession session, string label) => Rooms.Find(session, Fixtures.Id(label))!;

    [Fact]
    public void Ensure_installs_missing_triggers_and_keeps_accurate_counts()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        var room = GetRoom(session, "designers");
        var before = room.MessagesCount;

        RoomMessagesCount.Uninstall(session);
        Assert.False(RoomMessagesCount.TriggersInstalled(session));

        RoomMessagesCount.Ensure(session);
        Assert.True(RoomMessagesCount.TriggersInstalled(session));

        var reloaded = GetRoom(session, "designers");
        Assert.Equal(before, reloaded.MessagesCount);
        Assert.Equal(Messages.CountInRoom(session, room.Id), reloaded.MessagesCount);

        Messages.Create(session, room.Id, User("jason"), "count-ensure", Now);

        var afterCreate = GetRoom(session, "designers");
        Assert.Equal(before + 1, afterCreate.MessagesCount);
        Assert.Equal(Messages.CountInRoom(session, room.Id), afterCreate.MessagesCount);
    }

    [Fact]
    public void Ensure_repairs_a_partial_trigger_install_and_backfills_drifted_counts()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        var room = GetRoom(session, "designers");
        RoomMessagesCount.Uninstall(session);

        // Manually install only the INSERT trigger
        session.Execute($"""
            CREATE TRIGGER {RoomMessagesCount.InsertTrigger} AFTER INSERT ON messages
            BEGIN
              UPDATE rooms SET messages_count = messages_count + 1 WHERE id = NEW.room_id;
            END
            """);

        Assert.True(RoomMessagesCount.TriggerInstalled(session, RoomMessagesCount.InsertTrigger));
        Assert.False(RoomMessagesCount.TriggerInstalled(session, RoomMessagesCount.DeleteTrigger));

        // Create a message (trigger fires: +1), then delete directly (no delete trigger: counter drifts high)
        var drifted = Messages.Create(session, room.Id, User("jason"), "count-partial-drift", Now);
        session.Execute("DELETE FROM messages WHERE id = @id", ("@id", drifted.Id));

        var driftedRoom = GetRoom(session, "designers");
        Assert.True(driftedRoom.MessagesCount > Messages.CountInRoom(session, room.Id));

        RoomMessagesCount.Ensure(session);

        Assert.True(RoomMessagesCount.TriggersInstalled(session));
        var repairedRoom = GetRoom(session, "designers");
        Assert.Equal(Messages.CountInRoom(session, room.Id), repairedRoom.MessagesCount);
    }

    [Fact]
    public void Ensure_backfills_writes_that_landed_while_insert_trigger_was_missing()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        var room = GetRoom(session, "designers");
        var beforeCount = Messages.CountInRoom(session, room.Id);
        var beforeCached = room.MessagesCount;
        Assert.Equal(beforeCount, beforeCached);

        RoomMessagesCount.Uninstall(session);

        // Write directly to messages while trigger is missing
        session.Execute("""
            INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at)
            VALUES (@room_id, @creator_id, 'count-missing-insert', @now, @now)
            """,
            ("@room_id", room.Id),
            ("@creator_id", User("david")),
            ("@now", Db.Time(Now)));

        Assert.Equal(beforeCount + 1, Messages.CountInRoom(session, room.Id));
        Assert.Equal(beforeCached, GetRoom(session, "designers").MessagesCount);

        RoomMessagesCount.Ensure(session);

        Assert.True(RoomMessagesCount.TriggersInstalled(session));
        var repairedRoom = GetRoom(session, "designers");
        Assert.Equal(Messages.CountInRoom(session, room.Id), repairedRoom.MessagesCount);
        Assert.Equal(beforeCached + 1, repairedRoom.MessagesCount);
    }

    [Fact]
    public async Task Concurrent_ensure_repairs_leave_triggers_installed_and_counts_accurate()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using (var setupSession = new SqliteSession(SqliteConnections.Open(file.Path), 10))
        {
            var room = GetRoom(setupSession, "designers");
            RoomMessagesCount.Uninstall(setupSession);

            setupSession.Execute("""
                INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at)
                VALUES (@room_id, @creator_id, 'count-concurrent-drift', @now, @now)
                """,
                ("@room_id", room.Id),
                ("@creator_id", User("david")),
                ("@now", Db.Time(Now)));

            Assert.NotEqual(Messages.CountInRoom(setupSession, room.Id), GetRoom(setupSession, "designers").MessagesCount);
        }

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);
                RoomMessagesCount.Ensure(session);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }, Ct)).ToArray();

        await Task.WhenAll(workers);

        Assert.Empty(exceptions);

        using var verifySession = new SqliteSession(SqliteConnections.Open(file.Path), 10);
        Assert.True(RoomMessagesCount.TriggersInstalled(verifySession));
        var verifiedRoom = GetRoom(verifySession, "designers");
        Assert.Equal(Messages.CountInRoom(verifySession, verifiedRoom.Id), verifiedRoom.MessagesCount);
    }

    [Fact]
    public async Task Writes_concurrent_with_ensure_repair_leave_accurate_counts()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using (var setupSession = new SqliteSession(SqliteConnections.Open(file.Path), 10))
        {
            RoomMessagesCount.Uninstall(setupSession);
        }

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var ensureTask = Task.Run(() =>
        {
            try
            {
                using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);
                RoomMessagesCount.Ensure(session);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }, Ct);

        var insertTask = Task.Run(() =>
        {
            try
            {
                using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);
                WriteTransaction.Run(session, tx =>
                {
                    tx.Session.Execute("""
                        INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at)
                        VALUES (@room_id, @creator_id, 'count-during-repair', @now, @now)
                        """,
                        ("@room_id", Fixtures.Id("designers")),
                        ("@creator_id", User("david")),
                        ("@now", Db.Time(Now)));
                    return true;
                });
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }, Ct);

        await Task.WhenAll(ensureTask, insertTask);
        Assert.Empty(exceptions);

        using var verifySession = new SqliteSession(SqliteConnections.Open(file.Path), 10);
        RoomMessagesCount.Ensure(verifySession);
        Assert.True(RoomMessagesCount.TriggersInstalled(verifySession));
        var designers = GetRoom(verifySession, "designers");
        Assert.Equal(Messages.CountInRoom(verifySession, designers.Id), designers.MessagesCount);
        Assert.NotNull(verifySession.Scalar<long?>("SELECT id FROM messages WHERE client_message_id = 'count-during-repair'"));
    }

    [Fact]
    public void Foreign_sqlite_connections_keep_the_counter_in_step()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var conn = SqliteConnections.Open(file.Path);
        using var session = new SqliteSession(conn, 10);

        var room = GetRoom(session, "designers");
        var otherRoom = GetRoom(session, "pets");
        var before = room.MessagesCount;
        var otherBefore = otherRoom.MessagesCount;

        // Foreign INSERT
        session.Execute("""
            INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at)
            VALUES (@room_id, @creator_id, 'count-foreign-insert', @now, @now)
            """,
            ("@room_id", room.Id),
            ("@creator_id", User("david")),
            ("@now", Db.Time(Now)));

        var afterInsert = GetRoom(session, "designers");
        Assert.Equal(before + 1, afterInsert.MessagesCount);
        Assert.Equal(Messages.CountInRoom(session, room.Id), afterInsert.MessagesCount);

        var foreignId = session.Scalar<long>("SELECT id FROM messages WHERE client_message_id = 'count-foreign-insert'");

        // Foreign UPDATE room_id
        session.Execute("UPDATE messages SET room_id = @other_room WHERE id = @id",
            ("@other_room", otherRoom.Id), ("@id", foreignId));

        var afterMoveDesigners = GetRoom(session, "designers");
        var afterMovePets = GetRoom(session, "pets");
        Assert.Equal(before, afterMoveDesigners.MessagesCount);
        Assert.Equal(otherBefore + 1, afterMovePets.MessagesCount);

        // Foreign DELETE
        session.Execute("DELETE FROM messages WHERE id = @id", ("@id", foreignId));

        var afterDeleteDesigners = GetRoom(session, "designers");
        var afterDeletePets = GetRoom(session, "pets");
        Assert.Equal(before, afterDeleteDesigners.MessagesCount);
        Assert.Equal(otherBefore, afterDeletePets.MessagesCount);
    }

    [Fact]
    public void Message_indexes_exist_and_query_plans_use_them()
    {
        using var file = TestDatabase.FromParitySeed();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        // Verify index existence in sqlite_master
        var indexNames = session.Query(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'messages'",
            reader => reader.GetString(0));

        Assert.Contains("index_messages_on_room_id_and_created_at", indexNames);
        Assert.Contains("index_messages_on_room_id_and_updated_at", indexNames);

        // Verify query plan uses created_at index for room pagination
        var createdPlan = string.Join(" ", session.Query(
            "EXPLAIN QUERY PLAN SELECT id FROM messages WHERE room_id = 1 ORDER BY created_at DESC LIMIT 40",
            reader => reader.GetString(3)));
        Assert.Contains("index_messages_on_room_id_and_created_at", createdPlan);
        Assert.DoesNotContain("TEMP B-TREE", createdPlan);

        // Verify query plan uses updated_at index for recent updates query
        var updatedPlan = string.Join(" ", session.Query(
            "EXPLAIN QUERY PLAN SELECT id FROM messages WHERE room_id = 1 AND updated_at > '2026-03-01 00:00:00' ORDER BY updated_at",
            reader => reader.GetString(3)));
        Assert.Contains("index_messages_on_room_id_and_updated_at", updatedPlan);
    }

    [Fact]
    public void State_differential_seed_counts_and_schema_parity()
    {
        using var file = TestDatabase.FromParitySeed();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        // All Talk: 131 in seed, Designers: 31 in seed
        var allTalk = GetRoom(session, "watercooler");
        Assert.Equal(131, allTalk.MessagesCount);

        var designers = GetRoom(session, "designers");
        Assert.Equal(31, designers.MessagesCount);

        Assert.True(RoomMessagesCount.TriggersInstalled(session));
    }
}
