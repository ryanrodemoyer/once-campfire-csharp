using Microsoft.Data.Sqlite;

namespace Campfire.Data.Checkpoint;

// Copies WAL pages into the database off the request threads, as Rails' SqliteWalCheckpoint does
// (reference/lib/rails_ext/sqlite_wal_checkpoint.rb). database.yml turns SQLite's auto-checkpoint
// off, so no commit fsyncs the database on the writer. Instead one process at a time, the holder
// of an exclusive flock on the lock file, runs PASSIVE checkpoints every interval. Each process
// that opens the database starts a contender, which takes over when the holder exits.
//
// Forking servers must Stop before fork and Start again in the child, so the flock is never shared
// through an inherited file descriptor.
public class SqliteWalCheckpoint
{
    // SqliteWalCheckpoint::INTERVAL, MAX_BACKOFF and STOP_TIMEOUT.
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    // The checkpoint connection's busy handler: bounds retries, not PASSIVE checkpoint I/O.
    const int busyTimeoutMilliseconds = 1000;

    public const string ThreadName = "sqlite-wal-checkpoint";

    readonly string databasePath;
    readonly string lockPath;
    readonly Action<string>? warn;
    readonly object lifecycle = new();
    Thread? thread;
    TaskCompletionSource? stopping;
    bool exitCheckpointInstalled;

    public SqliteWalCheckpoint(string databasePath, string lockPath, Action<string>? warn = null)
    {
        this.databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        this.lockPath = lockPath ?? throw new ArgumentNullException(nameof(lockPath));
        this.warn = warn;
    }

    // Raised when Stop gives up waiting for the contender to release its connection and lock.
    public sealed class StopTimeoutException(string message) : Exception(message);

    public enum CheckpointTick
    {
        Busy,
        Checkpointed,
        NoDatabase,
    }

    public readonly record struct WalCheckpointResult(int Busy, int Log, int Checkpointed);

    internal bool ExitCheckpointInstalled
    {
        get
        {
            lock (lifecycle)
            {
                return exitCheckpointInstalled;
            }
        }
    }

    // Starts the contender, or returns the running one.
    public Thread Start(TimeSpan? interval = null)
    {
        lock (lifecycle)
        {
            if (thread is { IsAlive: true })
            {
                if (stopping!.Task.IsCompleted)
                {
                    throw new StopTimeoutException("SQLite WAL checkpointer is still stopping");
                }
                return thread;
            }

            stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            InstallExitCheckpoint();
            var signal = stopping.Task;
            var pollInterval = interval ?? DefaultInterval;
            thread = new Thread(() => Run(pollInterval, signal)) { Name = ThreadName, IsBackground = true };
            thread.Start();
            return thread;
        }
    }

    // Signals the contender to exit and waits until it has released the flock and closed its
    // connection. Callers that fork must not continue while those are still open.
    public void Stop(TimeSpan? timeout = null)
    {
        lock (lifecycle)
        {
            if (thread is null || stopping is null)
            {
                return;
            }
            stopping.TrySetResult();
            if (!thread.Join(timeout ?? DefaultStopTimeout))
            {
                throw new StopTimeoutException($"SQLite WAL checkpointer did not stop within {timeout ?? DefaultStopTimeout}");
            }
            thread = null;
            stopping = null;
        }
    }

    // One PASSIVE checkpoint on a fresh connection, without taking the lock. Null when there is no
    // database file.
    public WalCheckpointResult? Checkpoint()
    {
        using var connection = OpenDatabase();
        return connection is null ? null : CheckpointOn(connection);
    }

    // One leadership attempt: take the lock, checkpoint once, release.
    public CheckpointTick Tick()
    {
        using var leadership = FileLock.TryAcquire(lockPath);
        if (leadership is null)
        {
            return CheckpointTick.Busy;
        }
        return Checkpoint() is null ? CheckpointTick.NoDatabase : CheckpointTick.Checkpointed;
    }

    internal virtual WalCheckpointResult CheckpointOn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new WalCheckpointResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    void Run(TimeSpan interval, Task stop)
    {
        var backoff = interval;
        while (!stop.IsCompleted)
        {
            try
            {
                if (!File.Exists(databasePath))
                {
                    Wait(interval, stop);
                    continue;
                }

                using var leadership = FileLock.TryAcquire(lockPath);
                if (leadership is null)
                {
                    Wait(interval, stop);
                    continue;
                }

                using var connection = OpenDatabase();
                while (connection is not null && !stop.IsCompleted)
                {
                    CheckpointOn(connection);
                    backoff = interval;
                    Wait(interval, stop);
                }

                // The database vanished between the check and the open. Wait before retrying so the
                // lock file isn't spun on. When the loop ended because of a stop, this returns at once.
                Wait(interval, stop);
            }
#pragma warning disable CA1031 // A failed checkpoint is logged and retried, never allowed to end the contender.
            catch (Exception error)
#pragma warning restore CA1031
            {
                warn?.Invoke($"SQLite WAL checkpoint failed: {error.GetType().Name}: {error.Message}");
                Wait(backoff, stop);
                backoff = Min(backoff * 2, MaxBackoff);
            }
        }
    }

    static void Wait(TimeSpan timeout, Task stop) => _ = stop.Wait(timeout);

    static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    SqliteConnection? OpenDatabase()
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {busyTimeoutMilliseconds}";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Best-effort PASSIVE checkpoint when the process exits, for short-lived writers that exit before
    // a contender takes the lock. It doesn't touch lock ownership.
    void InstallExitCheckpoint()
    {
        if (exitCheckpointInstalled)
        {
            return;
        }
        exitCheckpointInstalled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CheckpointAtExit();
    }

    void CheckpointAtExit()
    {
        try
        {
            Checkpoint();
        }
#pragma warning disable CA1031 // Exit is not the place to report a failed checkpoint.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
