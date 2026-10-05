using System.Threading.Channels;
using Campfire.Data.Schema;

namespace Campfire.Data.Sqlite;

public sealed record SqliteDatabaseOptions(string Path)
{
    // Reader connections. Rails' pool is `RAILS_MAX_THREADS`, 10 by default
    // (reference/config/database.yml).
    public int Readers { get; init; } = 10;

    // Writes that may wait for the writer before WriteAsync itself waits for room.
    public int WriteQueueCapacity { get; init; } = 256;

    public int StatementCacheCapacity { get; init; } = StatementCache.DefaultCapacity;

    // Run `db:prepare` on open: load the schema into an empty database, or refuse one with
    // missing migrations.
    public bool Prepare { get; init; } = true;

    // ar_internal_metadata's `environment` when the schema is loaded.
    public string Environment { get; init; } = "production";

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

// The database: one writer and a pool of readers, all on the same file in WAL mode, so reads
// never wait for a write and see the last commit before they started.
//
// SQLite takes one writer at a time anyway, so writes queue for a single writer connection that
// a dedicated thread owns, each in its own immediate transaction. The queue is bounded: when it's
// full, WriteAsync waits for room. Reads take any free reader connection and run on the caller's
// thread; when every reader is busy, ReadAsync waits for one.
//
// A write must not wait on another write (or on a read that waits on one) from inside its own
// work: the writer thread would be waiting on itself.
public sealed class SqliteDatabase : IDisposable
{
    readonly Channel<Action<SqliteSession>> writes;
    readonly Thread writer;
    readonly SqliteSession writerSession;
    readonly Channel<SqliteSession> idleReaders;
    readonly List<SqliteSession> readers = [];
    bool disposed;

    SqliteDatabase(SqliteDatabaseOptions options, SqliteSession writerSession)
    {
        Path = options.Path;
        this.writerSession = writerSession;
        writes = Channel.CreateBounded<Action<SqliteSession>>(
            new BoundedChannelOptions(Math.Max(1, options.WriteQueueCapacity)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        idleReaders = Channel.CreateUnbounded<SqliteSession>();
        writer = new Thread(RunWriter) { Name = "campfire-db-writer", IsBackground = true };
    }

    public string Path { get; }

    public static SqliteDatabase Open(SqliteDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var writerSession = new SqliteSession(SqliteConnections.Open(options.Path), options.StatementCacheCapacity);
        SqliteDatabase? database = null;
        try
        {
            if (options.Prepare)
            {
                SchemaPreparer.Prepare(writerSession, options.Environment, options.Clock);
            }
            database = new SqliteDatabase(options, writerSession);
            for (var i = 0; i < Math.Max(1, options.Readers); i++)
            {
                var reader = new SqliteSession(SqliteConnections.Open(options.Path, readOnly: true), options.StatementCacheCapacity);
                database.readers.Add(reader);
                database.idleReaders.Writer.TryWrite(reader);
            }
            database.writer.Start();
            return database;
        }
        catch
        {
            if (database is null)
            {
                writerSession.Dispose();
            }
            else
            {
                database.Dispose();
            }
            throw;
        }
    }

    // Runs `work` as one immediate transaction on the writer thread.
    public async Task<T> WriteAsync<T>(Func<WriteTransaction, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await writes.Writer.WriteAsync(
            session =>
            {
                try
                {
                    result.SetResult(WriteTransaction.Run(session, work));
                }
#pragma warning disable CA1031 // Handed to the caller awaiting the write.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    result.SetException(exception);
                }
            },
            cancellationToken).ConfigureAwait(false);
        return await result.Task.ConfigureAwait(false);
    }

    public Task WriteAsync(Action<WriteTransaction> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WriteAsync(transaction =>
        {
            work(transaction);
            return true;
        }, cancellationToken);
    }

    // Runs `work` on a free reader connection, on the calling thread.
    public async Task<T> ReadAsync<T>(Func<SqliteSession, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var session = await idleReaders.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return work(session);
        }
        finally
        {
            idleReaders.Writer.TryWrite(session);
        }
    }

    void RunWriter()
    {
        while (writes.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (writes.Reader.TryRead(out var write))
            {
                write(writerSession);
            }
        }
    }

    // Finishes the queued writes, then closes every connection. Call it once reads have finished.
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        writes.Writer.TryComplete();
        if (writer.IsAlive)
        {
            writer.Join();
        }
        writerSession.Dispose();
        idleReaders.Writer.TryComplete();
        foreach (var reader in readers)
        {
            reader.Dispose();
        }
    }
}
