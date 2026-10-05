using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Tests.Sqlite;

public class SqliteDatabaseTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    static SqliteDatabase Open(TestDatabase file, int readers = 2, int writeQueue = 256) =>
        SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = readers, WriteQueueCapacity = writeQueue });

    static readonly string InsertMessage = """
        INSERT INTO messages (client_message_id, created_at, creator_id, room_id, updated_at)
        VALUES (@client_message_id, '2026-03-02 16:00:00', (SELECT min(id) FROM users), (SELECT min(id) FROM rooms), '2026-03-02 16:00:00')
        """;

    static int InsertMessageFrom(WriteTransaction transaction, string clientMessageId) =>
        transaction.Session.Execute(InsertMessage, ("@client_message_id", clientMessageId));

    static Task<long> CountMessages(SqliteDatabase database) =>
        database.ReadAsync(session => session.Scalar<long>("SELECT count(*) FROM messages"), Ct);

    static Dictionary<string, long> Pragmas(SqliteSession session) =>
        RailsPragmas.Keys.ToDictionary(pragma => pragma, pragma => session.Scalar<long>($"PRAGMA {pragma}"));

    // SQLite3Adapter::DEFAULT_PRAGMAS and `timeout: 5000`. synchronous=normal reads back as 1.
    static readonly Dictionary<string, long> RailsPragmas = new()
    {
        ["busy_timeout"] = 5000,
        ["foreign_keys"] = 1,
        ["synchronous"] = 1,
        ["mmap_size"] = 134217728,
        ["journal_size_limit"] = 67108864,
        ["cache_size"] = 2000,
    };

    [Fact]
    public async Task Writer_and_readers_use_the_rails_connection_settings()
    {
        using var file = new TestDatabase();
        using var database = Open(file);

        Assert.Equal(RailsPragmas, await database.WriteAsync(tx => Pragmas(tx.Session), Ct));
        Assert.Equal(RailsPragmas, await database.ReadAsync(Pragmas, Ct));
        Assert.Equal("wal", await database.ReadAsync(session => session.Scalar<string>("PRAGMA journal_mode"), Ct));
    }

    [Fact]
    public async Task Rails_seed_database_opens_read_write()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file);

        var users = await database.ReadAsync(session =>
            session.Query("SELECT name FROM users ORDER BY name", reader => reader.GetString(0)), Ct);
        Assert.Equal(["Bender Bot", "David", "JZ", "Jason", "Kevin"], users);
        Assert.Equal(13, await CountMessages(database));

        await database.WriteAsync(tx => InsertMessageFrom(tx, "csharp-1"), Ct);
        Assert.Equal(14, await CountMessages(database));
        Assert.Equal(1, await database.ReadAsync(session =>
            session.Scalar<long>("SELECT count(*) FROM messages WHERE client_message_id = @id", ("@id", "csharp-1")), Ct));

        // Foreign keys are enforced on Rails' data too.
        var error = await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync(tx =>
            tx.Session.Execute("UPDATE messages SET room_id = 0 WHERE client_message_id = 'csharp-1'"), Ct));
        Assert.Equal(19, error.SqliteErrorCode); // SQLITE_CONSTRAINT
        Assert.Empty(await database.ReadAsync(session => session.Query("PRAGMA foreign_key_check", reader => reader.GetString(0)), Ct));
    }

    [Fact]
    public async Task Concurrent_readers_do_not_block_the_writer()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file, readers: 4);
        using var snapshotsTaken = new CountdownEvent(4);
        using var writeDone = new ManualResetEventSlim();

        // Four readers each hold a read transaction open across the write.
        var reads = Enumerable.Range(0, 4).Select(_ => Task.Run(() => database.ReadAsync(session =>
        {
            session.Execute("BEGIN");
            var before = session.Scalar<long>("SELECT count(*) FROM messages");
            snapshotsTaken.Signal();
            Assert.True(writeDone.Wait(Patience, Ct));
            var during = session.Scalar<long>("SELECT count(*) FROM messages");
            session.Execute("COMMIT");
            return (before, during);
        }, Ct))).ToList();
        Assert.True(snapshotsTaken.Wait(Patience, Ct));

        var write = database.WriteAsync(tx => InsertMessageFrom(tx, "while-reading"), Ct);
        // Well inside the 5s busy timeout: the write didn't wait for the readers.
        Assert.Same(write, await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(2), Ct)));
        await write;
        writeDone.Set();

        // Each reader kept the snapshot it started with.
        Assert.All(await Task.WhenAll(reads), counts => Assert.Equal((13L, 13L), counts));
        Assert.Equal(14, await CountMessages(database));
    }

    [Fact]
    public async Task Writes_take_the_write_lock_when_they_begin()
    {
        using var file = new TestDatabase();
        using var database = Open(file);
        using var other = new SqliteConnection($"Data Source={file.Path};Pooling=False;Default Timeout=1");
        other.Open();

        // Before the write does anything, another connection can't start writing: BEGIN IMMEDIATE.
        var error = await database.WriteAsync(_ =>
        {
            using var begin = other.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            return Assert.Throws<SqliteException>(() => begin.ExecuteNonQuery());
        }, Ct);
        Assert.Equal(5, error.SqliteErrorCode); // SQLITE_BUSY
    }

    [Fact]
    public async Task Failed_write_rolls_back_and_skips_its_after_commit_work()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file);
        var afterCommitRan = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync(tx =>
        {
            InsertMessageFrom(tx, "rolled-back");
            tx.AfterCommit(_ => afterCommitRan = true);
            throw new InvalidOperationException("boom");
        }, Ct));

        Assert.False(afterCommitRan);
        Assert.Equal(13, await CountMessages(database));
        await database.WriteAsync(tx => InsertMessageFrom(tx, "next"), Ct);
        Assert.Equal(14, await CountMessages(database));
    }

    [Fact]
    public async Task After_commit_work_runs_in_order_once_committed()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file);
        var log = new List<string>();

        await database.WriteAsync(tx =>
        {
            InsertMessageFrom(tx, "committed");
            tx.AfterCommit(_ => log.Add("first: " + CountMessages(database).GetAwaiter().GetResult()));
            tx.AfterCommit(_ => log.Add("second"));
            log.Add("in transaction");
        }, Ct);

        // The reader in the first callback already sees the commit.
        Assert.Equal(["in transaction", "first: 14", "second"], log);
    }

    [Fact]
    public async Task Failing_after_commit_work_does_not_stop_the_rest()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file);
        var secondRan = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync(tx =>
        {
            InsertMessageFrom(tx, "kept");
            tx.AfterCommit(_ => throw new InvalidOperationException("callback"));
            tx.AfterCommit(_ => secondRan = true);
        }, Ct));

        Assert.True(secondRan);
        Assert.Equal(14, await CountMessages(database));
    }

    [Fact]
    public async Task Writes_from_many_callers_all_land_through_a_small_queue()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file, writeQueue: 2);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => database.WriteAsync(tx => InsertMessageFrom(tx, $"many-{i}"), Ct), Ct)));

        Assert.Equal(213, await CountMessages(database));
    }

    [Fact]
    public async Task Readers_cannot_write()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = Open(file);

        var error = await Assert.ThrowsAsync<SqliteException>(() => database.ReadAsync(session => session.Execute(InsertMessage, ("@client_message_id", "x")), Ct));
        Assert.Equal(8, error.SqliteErrorCode); // SQLITE_READONLY
    }

    [Fact]
    public async Task Disposing_finishes_queued_writes()
    {
        using var file = new TestDatabase();
        var database = Open(file);
        using var gate = new ManualResetEventSlim();

        var blocked = database.WriteAsync(_ => gate.Wait(Patience, Ct), Ct);
        var queued = Enumerable.Range(0, 5)
            .Select(i => (Task)database.WriteAsync(tx => tx.Session.Execute("INSERT INTO message_search_index (rowid, body) VALUES (@id, 'x')", ("@id", i + 1)), Ct))
            .ToList();
        gate.Set();
        database.Dispose();
        await Task.WhenAll(queued.Append(blocked));

        using var reopened = Open(file);
        Assert.Equal(5, await reopened.ReadAsync(session => session.Scalar<long>("SELECT count(*) FROM message_search_index"), Ct));
    }
}
