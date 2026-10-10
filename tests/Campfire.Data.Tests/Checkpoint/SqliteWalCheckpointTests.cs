using System.Collections.Concurrent;
using System.Diagnostics;
using Campfire.Data.Checkpoint;
using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Tests.Checkpoint;

// reference/test/lib/sqlite_wal_checkpoint_test.rb. Rails stubs the checkpoint and spawns lock holders
// in other processes. Here a subclass blocks inside the checkpoint, and a second flock on the same
// lock file stands in for the other process: flock conflicts between open file descriptions, so a
// second one in this process behaves as a second process does.
public class SqliteWalCheckpointTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    static readonly string InsertMessage = """
        INSERT INTO messages (client_message_id, created_at, creator_id, room_id, updated_at)
        VALUES (@client_message_id, '2026-03-02 16:00:00', (SELECT min(id) FROM users), (SELECT min(id) FROM rooms), '2026-03-02 16:00:00')
        """;

    [Fact]
    public void Connections_disable_WAL_auto_checkpoint()
    {
        using var file = new TestDatabase();
        using var connection = SqliteConnections.Open(file.Path);

        Assert.Equal(0, Scalar(connection, "PRAGMA wal_autocheckpoint"));
    }

    [Fact]
    public void A_passive_checkpoint_against_the_primary_database_does_not_raise()
    {
        using var file = TestDatabase.FromRailsFixtures();

        Assert.NotNull(Checkpointer(file).Checkpoint());
    }

    [Fact]
    public void Start_runs_a_named_contender_thread_that_stop_joins()
    {
        using var file = new TestDatabase();
        var checkpoint = Checkpointer(file);

        var thread = checkpoint.Start(TimeSpan.FromMilliseconds(50));
        Assert.True(thread.IsAlive);
        Assert.Equal(SqliteWalCheckpoint.ThreadName, thread.Name);

        checkpoint.Stop();
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public void Stop_before_start_again_mimics_a_fork_safe_worker_boot()
    {
        using var file = BuildWalDatabase(rows: 10);
        var checkpoint = Checkpointer(file);

        var first = checkpoint.Start(TimeSpan.FromMilliseconds(50));
        checkpoint.Stop();
        Assert.False(first.IsAlive);

        var second = checkpoint.Start(TimeSpan.FromMilliseconds(50));
        Assert.True(second.IsAlive);
        checkpoint.Stop();
        Assert.False(second.IsAlive);

        Assert.Equal(SqliteWalCheckpoint.CheckpointTick.Checkpointed, checkpoint.Tick());
    }

    [Fact]
    public void Stop_interrupts_a_long_checkpoint_interval()
    {
        using var file = BuildWalDatabase(rows: 10);
        using var checkpoint = new BlockingCheckpoint(file.Path, LockPath(file), blocked: false);
        var contender = checkpoint.Start(TimeSpan.FromSeconds(30));
        checkpoint.WaitUntilEntered(Patience);

        var clock = Stopwatch.StartNew();
        checkpoint.Stop(TimeSpan.FromSeconds(2));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(contender.IsAlive);
        Assert.False(LockHeld(file));
    }

    [Fact]
    public void Stop_interrupts_checkpoint_error_backoff()
    {
        using var file = new TestDatabase();
        File.WriteAllBytes(file.Path, new byte[4096]);
        var warnings = new ConcurrentQueue<string>();
        var checkpoint = new SqliteWalCheckpoint(file.Path, LockPath(file), warnings.Enqueue);
        var contender = checkpoint.Start(TimeSpan.FromSeconds(30));
        WaitUntil(() => !warnings.IsEmpty);

        checkpoint.Stop(TimeSpan.FromSeconds(2));

        Assert.False(contender.IsAlive);
        Assert.False(LockHeld(file));
    }

    [Fact]
    public void Timed_out_stop_prevents_replacement_until_the_checkpoint_releases_its_resources()
    {
        using var file = BuildWalDatabase(rows: 10);
        using var blocking = new BlockingCheckpoint(file.Path, LockPath(file));
        var contender = blocking.Start();
        blocking.WaitUntilEntered(Patience);
        Assert.True(LockHeld(file));

        Assert.Throws<SqliteWalCheckpoint.StopTimeoutException>(() => blocking.Stop(TimeSpan.FromMilliseconds(10)));
        Assert.True(contender.IsAlive);
        Assert.True(LockHeld(file));
        Assert.Throws<SqliteWalCheckpoint.StopTimeoutException>(() => blocking.Start());

        blocking.Release();
        blocking.Stop();
        Assert.False(contender.IsAlive);
        Assert.False(LockHeld(file));
    }

    [Fact]
    public async Task Start_waits_for_a_concurrent_stop_to_release_the_previous_contender()
    {
        using var file = BuildWalDatabase(rows: 10);
        using var blocking = new BlockingCheckpoint(file.Path, LockPath(file));
        var contender = blocking.Start();
        blocking.WaitUntilEntered(Patience);

        var stopping = Task.Run(() => blocking.Stop(), Ct);
        await Task.Delay(50, Ct);
        var starting = Task.Run(() => blocking.Start(), Ct);
        await Task.Delay(50, Ct);
        Assert.True(contender.IsAlive);

        blocking.Release();
        await stopping;
        var replacement = await starting;

        Assert.False(contender.IsAlive);
        Assert.True(replacement.IsAlive);
        Assert.NotEqual(contender, replacement);
    }

    [Fact]
    public async Task A_contender_cannot_release_a_concurrent_ticks_lock()
    {
        using var file = BuildWalDatabase(rows: 10);
        using var blocking = new BlockingCheckpoint(file.Path, LockPath(file));
        var ticking = Task.Run(() => blocking.Tick(), Ct);
        blocking.WaitUntilEntered(Patience);

        var contender = blocking.Start(TimeSpan.FromSeconds(30));
        blocking.Stop(TimeSpan.FromSeconds(2));

        Assert.False(contender.IsAlive);
        Assert.True(LockHeld(file));
        blocking.Release();
        Assert.Equal(SqliteWalCheckpoint.CheckpointTick.Checkpointed, await ticking);
        Assert.False(LockHeld(file));
    }

    [Fact]
    public void Start_installs_the_process_exit_checkpoint_once()
    {
        using var file = new TestDatabase();
        var checkpoint = Checkpointer(file);
        Assert.False(checkpoint.ExitCheckpointInstalled);

        checkpoint.Start(TimeSpan.FromMilliseconds(50));
        checkpoint.Stop();
        checkpoint.Start(TimeSpan.FromMilliseconds(50));
        checkpoint.Stop();

        Assert.True(checkpoint.ExitCheckpointInstalled);
    }

    [Fact]
    public void Tick_checkpoints_through_the_elected_lock_holder()
    {
        using var file = BuildWalDatabase(rows: 50);

        Assert.Equal(SqliteWalCheckpoint.CheckpointTick.Checkpointed, Checkpointer(file).Tick());
    }

    [Fact]
    public void Tick_reports_busy_while_another_holder_has_the_lock_and_succeeds_after_it_releases()
    {
        using var file = BuildWalDatabase(rows: 20);
        var checkpoint = Checkpointer(file);

        using (var holder = FileLock.TryAcquire(LockPath(file)))
        {
            Assert.NotNull(holder);
            Assert.Equal(SqliteWalCheckpoint.CheckpointTick.Busy, checkpoint.Tick());
        }

        Assert.Equal(SqliteWalCheckpoint.CheckpointTick.Checkpointed, checkpoint.Tick());
    }

    [Fact]
    public void Passive_checkpoint_moves_WAL_pages_after_writes_with_autocheckpoint_disabled()
    {
        using var file = new TestDatabase();
        using var writer = OpenWalWriter(file);
        for (var i = 0; i < 200; i++)
        {
            Insert(writer, $"row-{i}-{new string('x', 200)}");
        }
        Assert.True(WalSize(file.Path) > 0, "expected a WAL file after inserts");

        var result = Checkpointer(file).Checkpoint();

        Assert.NotNull(result);
        Assert.True(result.Value.Checkpointed > 0, $"expected PASSIVE checkpoint to copy WAL pages, got {result.Value}");
    }

    [Fact]
    public void A_contender_takes_over_checkpointing_after_the_lock_holder_stops()
    {
        using var file = BuildWalDatabase(rows: 30);
        var checkpoint = Checkpointer(file);
        var holder = FileLock.TryAcquire(LockPath(file));
        Assert.NotNull(holder);

        var contender = checkpoint.Start(TimeSpan.FromMilliseconds(50));
        try
        {
            Assert.Equal(SqliteWalCheckpoint.CheckpointTick.Busy, checkpoint.Tick());
            holder.Dispose();
            holder = null;

            WaitUntil(() => LockHeld(file));
        }
        finally
        {
            holder?.Dispose();
            checkpoint.Stop();
            Assert.False(contender.IsAlive);
        }
    }

    [Fact]
    public async Task WAL_size_stays_bounded_under_a_sustained_posting_run_and_stop_leaves_the_database_consistent()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        var checkpoint = Checkpointer(file);
        var contender = checkpoint.Start(TimeSpan.FromMilliseconds(20));

        // About 1,000 posts a second, each its own commit as each request is in the app. Unpaced, a
        // burst lands inside one checkpoint interval and the WAL can't restart, so the bound would
        // measure the burst rather than the checkpointer.
        var largestWal = 0L;
        const int ticks = 100;
        const int postsPerTick = 10;
        for (var tick = 0; tick < ticks; tick++)
        {
            for (var post = 0; post < postsPerTick; post++)
            {
                var id = $"sustained-{tick * postsPerTick + post}";
                await database.WriteAsync(transaction =>
                    transaction.Session.Execute(InsertMessage, ("@client_message_id", id)), Ct);
            }
            largestWal = Math.Max(largestWal, WalSize(file.Path));
            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }

        checkpoint.Stop();
        Assert.False(contender.IsAlive);

        Assert.True(largestWal < 2 * WriterCheckpointFallback.ThresholdBytes, $"WAL grew to {largestWal} bytes");
        var messages = await database.ReadAsync(session => session.Scalar<long>("SELECT count(*) FROM messages"), Ct);
        Assert.Equal(13 + ticks * postsPerTick, messages);
        var integrity = await database.ReadAsync(session => session.Scalar<string>("PRAGMA integrity_check"), Ct);
        Assert.Equal("ok", integrity);
    }

    [Fact]
    public async Task Stop_while_posting_exits_promptly_and_leaves_the_database_consistent()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        var checkpoint = Checkpointer(file);
        var contender = checkpoint.Start(TimeSpan.FromMilliseconds(1));
        using var stopPosting = new CancellationTokenSource();
        var posted = 0;
        var posting = Task.Run(async () =>
        {
            for (var i = 0; !stopPosting.IsCancellationRequested; i++)
            {
                await database.WriteAsync(transaction =>
                    transaction.Session.Execute(InsertMessage, ("@client_message_id", $"shutdown-{i}")), Ct);
                posted = i + 1;
            }
        }, Ct);
        await Task.Delay(300, Ct);

        var clock = Stopwatch.StartNew();
        checkpoint.Stop(TimeSpan.FromSeconds(2));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(contender.IsAlive);

        await stopPosting.CancelAsync();
        await posting;
        Assert.True(posted > 0);
        var messages = await database.ReadAsync(session => session.Scalar<long>("SELECT count(*) FROM messages"), Ct);
        Assert.Equal(13 + posted, messages);
        var integrity = await database.ReadAsync(session => session.Scalar<string>("PRAGMA integrity_check"), Ct);
        Assert.Equal("ok", integrity);
    }

    [Fact]
    public void The_writer_checkpoints_once_the_WAL_passes_the_fallback_threshold()
    {
        using var file = new TestDatabase();
        using var writer = OpenWalWriter(file);
        using var session = new SqliteSession(writer, statementCacheCapacity: 8);
        var mainBefore = new FileInfo(file.Path).Length;
        InsertUntilWalExceeds(writer, WriterCheckpointFallback.ThresholdBytes);
        Assert.Equal(mainBefore, new FileInfo(file.Path).Length);

        WriterCheckpointFallback.CheckpointIfLarge(session, file.Path);

        Assert.True(new FileInfo(file.Path).Length > mainBefore, "expected the writer's checkpoint to copy WAL pages into the database");
    }

    [Fact]
    public void The_writer_leaves_a_small_WAL_alone()
    {
        using var file = new TestDatabase();
        using var writer = OpenWalWriter(file);
        using var session = new SqliteSession(writer, statementCacheCapacity: 8);
        Insert(writer, "small");
        var mainBefore = new FileInfo(file.Path).Length;

        WriterCheckpointFallback.CheckpointIfLarge(session, file.Path);

        Assert.Equal(mainBefore, new FileInfo(file.Path).Length);
    }

    static void InsertUntilWalExceeds(SqliteConnection writer, long bytes)
    {
        for (var i = 0; WalSize(writer.DataSource) <= bytes; i++)
        {
            Insert(writer, $"row-{i}-{new string('x', 1000)}");
        }
    }

    static SqliteWalCheckpoint Checkpointer(TestDatabase file) => new(file.Path, LockPath(file));

    static string LockPath(TestDatabase file) => Path.Combine(Path.GetDirectoryName(file.Path)!, "checkpoint.lock");

    // Whether another holder has the lock. The probe takes it briefly, which is fine: the contender
    // only ever sees it busy while the probe holds it, and the test is single-threaded here.
    static bool LockHeld(TestDatabase file)
    {
        using var probe = FileLock.TryAcquire(LockPath(file));
        return probe is null;
    }

    static TestDatabase BuildWalDatabase(int rows)
    {
        var file = new TestDatabase();
        using var writer = OpenWalWriter(file);
        for (var i = 0; i < rows; i++)
        {
            Insert(writer, $"row-{i}-{new string('x', 200)}");
        }
        return file;
    }

    static SqliteConnection OpenWalWriter(TestDatabase file)
    {
        var writer = new SqliteConnection($"Data Source={file.Path}");
        writer.Open();
        Execute(writer, "PRAGMA journal_mode=WAL");
        Execute(writer, "PRAGMA wal_autocheckpoint=0");
        Execute(writer, "CREATE TABLE items (id INTEGER PRIMARY KEY, body TEXT)");
        return writer;
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    static void Insert(SqliteConnection connection, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO items (body) VALUES (@body)";
        command.Parameters.AddWithValue("@body", body);
        command.ExecuteNonQuery();
    }

    static long WalSize(string databasePath)
    {
        var wal = $"{databasePath}-wal";
        return File.Exists(wal) ? new FileInfo(wal).Length : 0;
    }

    static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met within the patience window");
            }
            Thread.Sleep(20);
        }
    }

    // A checkpointer that reports when its first checkpoint starts, with the lock held. When blocked,
    // that checkpoint waits until the test releases it, so a stop can time out while one is in flight.
    sealed class BlockingCheckpoint(string databasePath, string lockPath, bool blocked = true) : SqliteWalCheckpoint(databasePath, lockPath), IDisposable
    {
        readonly ManualResetEventSlim entered = new();
        readonly ManualResetEventSlim release = new(initialState: !blocked);
        int calls;

        public void WaitUntilEntered(TimeSpan timeout) =>
            Assert.True(entered.Wait(timeout), "expected the checkpoint to be entered");

        public void Release() => release.Set();

        internal override WalCheckpointResult CheckpointOn(SqliteConnection connection)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(Patience);
            }
            return base.CheckpointOn(connection);
        }

        public void Dispose()
        {
            release.Set();
            try
            {
                Stop();
            }
            catch (StopTimeoutException)
            {
            }
        }
    }
}
