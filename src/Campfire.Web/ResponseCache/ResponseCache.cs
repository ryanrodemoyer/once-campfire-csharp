using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.ResponseCache;

/// <summary>
/// A read-only observer of one SQLite file (reference/app/models/response_cache.rb).
/// <c>PRAGMA data_version</c> moves forward on every commit, including commits from another
/// process, and can only be compared on this same connection. A path change or
/// <see cref="Clear"/> drops the connection, so the next version carries a new namespace.
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

    SqliteConnection? observer;
    string? database;
    string? ns;
    long? dataVersion;
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

    /// <summary>
    /// <c>budget</c>: <c>CAMPFIRE_RESPONSE_CACHE_MB</c> (default 64, <c>to_i</c> then at least 0)
    /// mebibytes, read on every call. <see cref="BudgetOverride"/> replaces that, the way the
    /// model tests stub <c>budget</c>.
    /// </summary>
    public long Budget
    {
        get
        {
            if (BudgetOverride is long over)
            {
                return Math.Max(over, 0);
            }
            var raw = Environment.GetEnvironmentVariable("CAMPFIRE_RESPONSE_CACHE_MB");
            var megabytes = raw is null ? 64 : RubyToI(raw);
            return Math.Max(megabytes, 0) * 1024L * 1024L;
        }
    }

    /// <summary><c>version</c>: the database path, this connection's namespace and <c>data_version</c>.</summary>
    public CacheVersion? Version()
    {
        try
        {
            lock (mutex)
            {
                return CurrentUnlocked();
            }
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
            lock (mutex)
            {
                return CurrentUnlocked() == version && entries.TryGetValue(key, out var entry) ? entry.Page : null;
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
            lock (mutex)
            {
                if (CurrentUnlocked() != version || entries.ContainsKey(key))
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

    /// <summary><c>clear</c>: drop every entry and the observer, so the next version is a new namespace.</summary>
    public void Clear()
    {
        lock (mutex)
        {
            entries.Clear();
            order.Clear();
            usedBytes = 0;
            observer?.Dispose();
            observer = null;
            database = null;
            ns = null;
            dataVersion = null;
        }
    }

    // current_version, called with the mutex held.
    CacheVersion? CurrentUnlocked()
    {
        var path = Path.GetFullPath(databasePath());
        if (observer is null || database != path)
        {
            entries.Clear();
            order.Clear();
            usedBytes = 0;
            observer?.Dispose();
            observer = null;
            dataVersion = null;
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            observer = connection;
            database = path;
            ns = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }
        using var command = observer.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        var current = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (dataVersion != current)
        {
            entries.Clear();
            order.Clear();
            usedBytes = 0;
            dataVersion = current;
        }
        return new CacheVersion(database!, ns!, dataVersion!.Value);
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
