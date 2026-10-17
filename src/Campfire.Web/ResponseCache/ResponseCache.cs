using System.Collections.Concurrent;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.ResponseCache;

/// <summary>
/// Observes one SQLite file through read-only connections (reference/app/models/response_cache.rb).
/// <c>PRAGMA data_version</c> moves forward on every commit, including commits from another
/// process, and can only be compared on the connection that read it. Rails keeps one observer
/// connection and queries it under the cache mutex. Here, a database in WAL mode is observed by
/// comparing its WAL-index header under the mutex, which is what moves data_version (see
/// <see cref="WalIndex"/>). Otherwise each caller takes an observer from a
/// pool and queries it outside the mutex. An observer that sees its data_version move advances
/// the cache's generation and drops every entry, as Rails does when its one data_version moves;
/// a newly opened observer counts as moved, since commits before it opened went unseen. A path
/// change or <see cref="Clear"/> drops the observers, so the next version carries a new namespace.
/// </summary>
public sealed class ResponseCache
{
    /// <summary><c>MAX_ENTRY_BYTES</c>: one mebibyte.</summary>
    public const int MaxEntryBytes = 1024 * 1024;

    /// <summary><c>MAX_KEY_BYTES</c>: two kibibytes.</summary>
    public const int MaxKeyBytes = 2 * 1024;

    const int renderLockCount = 16;
    const int entryOverhead = 256;

    readonly object mutex = new();
    readonly Dictionary<string, (CachedPage Page, int Size)> entries = [];
    readonly Queue<string> order = new();
    readonly SemaphoreSlim[] renderLocks = [.. Enumerable.Range(0, renderLockCount).Select(_ => new SemaphoreSlim(1, 1))];
    readonly Func<string> databasePath;

    readonly ConcurrentBag<Observer> idleObservers = [];
    WalIndex? walIndex;
    long walIndexEpoch = -1;
    string? database;
    string? ns;
    long epoch;
    long generation;
    long usedBytes;
    int reads;
    int writes;

    public ResponseCache(Func<string> databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        this.databasePath = databasePath;
    }

    /// <summary>Test stand-in for <c>budget</c>, in bytes. Null reads <c>CAMPFIRE_RESPONSE_CACHE_MB</c>.</summary>
    public long? BudgetOverride { get; set; }

    /// <summary>How many times <see cref="Write"/> was entered, including a write that stored nothing.</summary>
    public int Writes => Volatile.Read(ref writes);

    /// <summary>How many times <see cref="Read"/> was entered.</summary>
    public int Reads => Volatile.Read(ref reads);

    /// <summary>Whether commits are observed through the WAL-index header rather than <c>PRAGMA data_version</c>.</summary>
    internal bool ObservesWalIndex
    {
        get
        {
            lock (mutex)
            {
                return walIndex is not null;
            }
        }
    }

    /// <summary>
    /// <c>budget</c>: <c>CAMPFIRE_RESPONSE_CACHE_MB</c> (default 64, <c>to_i</c> then at least 0)
    /// mebibytes. <see cref="BudgetOverride"/> replaces that, the way the model tests stub <c>budget</c>.
    /// </summary>
    /// <remarks>
    /// Rails reads <c>ENV</c> on every call. A running process's environment doesn't change (nothing here
    /// sets this variable), and a lookup on each of the five or so calls a request makes was about 4%
    /// of a busy room page's CPU, so the variable is read once.
    /// </remarks>
    public long Budget => BudgetOverride is long over ? Math.Max(over, 0) : EnvironmentBudget;

    static readonly long EnvironmentBudget = ParseBudget(Environment.GetEnvironmentVariable("CAMPFIRE_RESPONSE_CACHE_MB"));

    /// <summary>The bytes <c>CAMPFIRE_RESPONSE_CACHE_MB=<paramref name="raw"/></c> allows; unset is 64 MiB.</summary>
    internal static long ParseBudget(string? raw) => Math.Max(raw is null ? 64 : RubyToI(raw), 0) * 1024L * 1024L;

    /// <summary><c>version</c>: the database path, this connection's namespace and <c>data_version</c>.</summary>
    public CacheVersion? Version()
    {
        try
        {
            return Current();
        }
        catch (SqliteException)
        {
            Clear();
            return null;
        }
    }

    /// <summary><c>read</c>: the page stored for <paramref name="key"/> while <paramref name="version"/> is still current.</summary>
    public CachedPage? Read(string key, CacheVersion? version)
    {
        ArgumentNullException.ThrowIfNull(key);
        Interlocked.Increment(ref reads);
        try
        {
            Current();
            lock (mutex)
            {
                return IsCurrentUnlocked(version) && entries.TryGetValue(key, out var entry) ? entry.Page : null;
            }
        }
        catch (SqliteException)
        {
            Clear();
            return null;
        }
    }

    /// <summary>
    /// <c>synchronize_render</c>: one of 16 locks, so cold renders of the same page collapse.
    /// The callback must not be waited on from the thread that already holds it. It never holds
    /// <see cref="mutex"/>, so <see cref="Version"/> and <see cref="Read"/> still run.
    /// </summary>
    public void SynchronizeRender(string key, CacheVersion version, Action render)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(render);
        var gate = renderLocks[LockIndex(key, version)];
        gate.Wait();
        try
        {
            render();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary><see cref="SynchronizeRender"/> for a render that awaits.</summary>
    public async ValueTask SynchronizeRenderAsync(string key, CacheVersion version, Func<ValueTask> render)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(render);
        var gate = renderLocks[LockIndex(key, version)];
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await render().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// <c>write</c>: keep <paramref name="page"/> when <paramref name="version"/> is still current
    /// and the key is new. An entry larger than the budget or <see cref="MaxEntryBytes"/>, or a
    /// key over <see cref="MaxKeyBytes"/>, is not stored. Oldest entries leave first.
    /// </summary>
    public void Write(string key, CacheVersion? version, CachedPage page)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(page);
        Interlocked.Increment(ref writes);
        var size = Encoding.UTF8.GetByteCount(key) + page.Body.Length + HeaderBytes(page.Headers) + entryOverhead;
        if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes || size > Math.Min(Budget, MaxEntryBytes))
        {
            return;
        }
        try
        {
            Current();
            lock (mutex)
            {
                if (!IsCurrentUnlocked(version) || entries.ContainsKey(key))
                {
                    return;
                }
                var budget = Budget;
                while (order.Count > 0 && usedBytes + size > budget)
                {
                    var oldest = order.Dequeue();
                    if (entries.Remove(oldest, out var removed))
                    {
                        usedBytes -= removed.Size;
                    }
                }
                entries[key] = (page, size);
                order.Enqueue(key);
                usedBytes += size;
            }
        }
        catch (SqliteException)
        {
            Clear();
        }
    }

    /// <summary><c>clear</c>: drop every entry and the observers, so the next version is a new namespace.</summary>
    public void Clear()
    {
        lock (mutex)
        {
            ResetUnlocked();
        }
        DisposeIdleObservers();
    }

    // current_version: in WAL mode, compare the WAL-index header under the mutex. Otherwise observe
    // data_version on a pooled observer without holding the mutex, then advance the generation
    // under it if that observer saw a commit.
    CacheVersion Current()
    {
        while (true)
        {
            var path = Path.GetFullPath(databasePath());
            long observedEpoch;
            lock (mutex)
            {
                if (database != path || ns is null)
                {
                    ResetUnlocked();
                    database = path;
                    ns = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                }
                observedEpoch = epoch;
                if (walIndex is not null)
                {
                    if (walIndex.Observe())
                    {
                        generation++;
                        DropEntriesUnlocked();
                    }
                    return new CacheVersion(database, ns, generation);
                }
            }
            if (Volatile.Read(ref walIndexEpoch) != observedEpoch)
            {
                OpenWalIndex(path, observedEpoch);
                continue;
            }
            if (!idleObservers.TryTake(out var observer))
            {
                observer = new Observer(path, observedEpoch);
            }
            var healthy = false;
            try
            {
                var moved = observer.Epoch != observedEpoch || observer.Observe();
                healthy = observer.Epoch == observedEpoch;
                lock (mutex)
                {
                    if (epoch != observedEpoch || observer.Epoch != observedEpoch)
                    {
                        // A path change or Clear came between: observe again in the new namespace.
                        continue;
                    }
                    if (moved)
                    {
                        generation++;
                        DropEntriesUnlocked();
                    }
                    return new CacheVersion(database!, ns!, generation);
                }
            }
            finally
            {
                if (healthy && Volatile.Read(ref epoch) == observer.Epoch)
                {
                    idleObservers.Add(observer);
                }
                else
                {
                    observer.Dispose();
                }
            }
        }
    }

    // Tried once per namespace. Opening runs SQLite statements, so it happens outside the mutex;
    // when two callers race, the first one kept wins.
    void OpenWalIndex(string path, long observedEpoch)
    {
        var opened = WalIndex.Open(path);
        lock (mutex)
        {
            if (epoch == observedEpoch && walIndexEpoch != observedEpoch)
            {
                walIndex = opened;
                walIndexEpoch = observedEpoch;
                return;
            }
        }
        opened?.Dispose();
    }

    bool IsCurrentUnlocked(CacheVersion? version) =>
        version is not null && version.Database == database && version.Namespace == ns && version.DataVersion == generation;

    // A new namespace: observers from before it are disposed when they come back.
    void ResetUnlocked()
    {
        DropEntriesUnlocked();
        epoch++;
        database = null;
        ns = null;
        walIndex?.Dispose();
        walIndex = null;
    }

    void DropEntriesUnlocked()
    {
        entries.Clear();
        order.Clear();
        usedBytes = 0;
    }

    void DisposeIdleObservers()
    {
        while (idleObservers.TryTake(out var observer))
        {
            observer.Dispose();
        }
    }

    // One read-only connection and its prepared data_version query, with the last value it read.
    sealed class Observer : IDisposable
    {
        readonly SqliteConnection connection;
        readonly SqliteCommand command;
        long? seen;

        public Observer(string path, long epoch)
        {
            Epoch = epoch;
            connection = OpenReadOnly(path);
            try
            {
                command = connection.CreateCommand();
                command.CommandText = "PRAGMA data_version";
                command.Prepare();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public long Epoch { get; }

        // Whether data_version moved since this observer last read it; true on its first read.
        public bool Observe()
        {
            var current = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            var moved = seen != current;
            seen = current;
            return moved;
        }

        public void Dispose()
        {
            command?.Dispose();
            connection.Dispose();
        }
    }

    /// <summary>
    /// The WAL-index header in the <c>-shm</c> file (https://www.sqlite.org/walformat.html, "The
    /// WAL-Index Header"). Every commit rewrites it: the second copy, a barrier, then the first.
    /// A reader that begins a transaction compares the whole header with the copy it saw last,
    /// and a difference is what moves its data_version (sqlite3 <c>walIndexReadHdr</c> and
    /// <c>pagerBeginReadTransaction</c>). Comparing the header here reaches the same answer as
    /// <c>PRAGMA data_version</c> without running a statement, and it still sees commits from
    /// other processes. The connection stays open while the header is read: it keeps the
    /// database in WAL mode and the <c>-shm</c> file in place.
    /// </summary>
    sealed class WalIndex : IDisposable
    {
        const int headerBytes = 48;
        const int isInitOffset = 12;

        readonly SqliteConnection connection;
        readonly MemoryMappedFile file;
        readonly MemoryMappedViewAccessor view;
        readonly byte[] first = new byte[headerBytes];
        readonly byte[] second = new byte[headerBytes];
        readonly byte[] seen = new byte[headerBytes];
        bool hasSeen;

        WalIndex(SqliteConnection connection, MemoryMappedFile file, MemoryMappedViewAccessor view)
        {
            this.connection = connection;
            this.file = file;
            this.view = view;
        }

        // Null when the database is not in WAL mode or its -shm file can't be mapped.
        public static WalIndex? Open(string path)
        {
            var connection = OpenReadOnly(path);
            MemoryMappedFile? file = null;
            try
            {
                using var command = connection.CreateCommand();
                // A read transaction opens the WAL and the -shm file before journal_mode reports wal.
                command.CommandText = "PRAGMA data_version";
                command.ExecuteScalar();
                command.CommandText = "PRAGMA journal_mode";
                if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    connection.Dispose();
                    return null;
                }
                var stream = new FileStream(path + "-shm", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                return new WalIndex(connection, file, file.CreateViewAccessor(0, 2 * headerBytes, MemoryMappedFileAccess.Read));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                file?.Dispose();
                connection.Dispose();
                return null;
            }
            catch
            {
                file?.Dispose();
                connection.Dispose();
                throw;
            }
        }

        // Whether the header changed since the last call; true on the first call. Copies that
        // differ (a commit is writing them) or an uninitialized header count as moved and are
        // compared again next time.
        public bool Observe()
        {
            view.ReadArray(0, first, 0, headerBytes);
            Interlocked.MemoryBarrier();
            view.ReadArray(headerBytes, second, 0, headerBytes);
            if (!first.AsSpan().SequenceEqual(second) || first[isInitOffset] == 0)
            {
                hasSeen = false;
                return true;
            }
            var moved = !hasSeen || !first.AsSpan().SequenceEqual(seen);
            first.CopyTo(seen, 0);
            hasSeen = true;
            return moved;
        }

        public void Dispose()
        {
            view.Dispose();
            file.Dispose();
            connection.Dispose();
        }
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    static int HeaderBytes(IReadOnlyList<(string Name, string Value)> headers)
    {
        var bytes = 0;
        foreach (var (name, value) in headers)
        {
            bytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
        }
        return bytes;
    }

    static int LockIndex(string key, CacheVersion version)
    {
        var hash = HashCode.Combine(key, version.Database, version.Namespace, version.DataVersion);
        return (hash & int.MaxValue) % renderLockCount;
    }

    // String#to_i: an optional sign and the leading digits, else 0.
    static long RubyToI(string text)
    {
        var index = 0;
        var sign = 1L;
        if (index < text.Length && text[index] == '-')
        {
            sign = -1;
            index++;
        }
        else if (index < text.Length && text[index] == '+')
        {
            index++;
        }
        long value = 0;
        var any = false;
        for (; index < text.Length && text[index] is >= '0' and <= '9'; index++)
        {
            any = true;
            value = (value * 10) + (text[index] - '0');
        }
        return any ? sign * value : 0;
    }
}

/// <summary><c>version</c>: <c>[database path, namespace, data_version]</c>.</summary>
public sealed record CacheVersion(string Database, string Namespace, long DataVersion)
{
    /// <summary>The version as it is spelled inside a fragment or record key.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Database}\n{Namespace}\n{DataVersion}");
}

/// <summary>One cached page: the body that was stored and the headers snapshotted with it.</summary>
public sealed class CachedPage(byte[] body, IReadOnlyList<(string Name, string Value)> headers)
{
    public byte[] Body { get; } = body;

    public IReadOnlyList<(string Name, string Value)> Headers { get; } = headers;
}
