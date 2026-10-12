using System.Text;
using Campfire.Data.Records;
using Campfire.Web.ResponseCache;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary>
/// <c>reference/test/models/record_cache_test.rb</c>. A hit rebuilds independent rows. A commit
/// during the lookup or the load, a disabled budget, a missing epoch, or an open transaction
/// returns the loader's own instances and stores nothing.
/// </summary>
public sealed class RecordCacheTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-record-cache", Guid.NewGuid().ToString("N"));
    readonly string database;
    readonly Campfire.Web.ResponseCache.ResponseCache cache;
    readonly FragmentStore store = new(64 * 1024);
    readonly Row record = new() { Name = "David" };

    public RecordCacheTests()
    {
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "records.sqlite3");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE changes_for_test (value TEXT);
                """;
            command.ExecuteNonQuery();
        }
        cache = new Campfire.Web.ResponseCache.ResponseCache(() => database) { BudgetOverride = 4096 };
    }

    [Fact]
    public void Hits_reconstruct_independent_rows_and_a_user_keeps_ticks_and_enums()
    {
        var version = cache.Version();
        Assert.Same(record, Fetch("user", version, () => [record])[0]);
        var first = Fetch("user", version, () => throw new InvalidOperationException("cache hit queried records"))[0];
        var second = Fetch("user", version, () => throw new InvalidOperationException("cache hit queried records"))[0];
        Assert.NotSame(record, first);
        Assert.NotSame(first, second);
        Assert.Equal(record.Name, first.Name);
        first.Name = "Request-local edit";
        Assert.Equal("David", second.Name);
        Assert.Equal("David", Fetch("user", version, () => throw new InvalidOperationException("cache hit queried records"))[0].Name);

        var created = new DateTimeOffset(2026, 10, 8, 12, 34, 56, TimeSpan.Zero).AddTicks(123456 * 10);
        var updated = new DateTimeOffset(2026, 10, 8, 12, 34, 57, TimeSpan.Zero).AddTicks(654321 * 10);
        var user = new User(1, "David", "david@example.com", "digest", UserRole.Administrator, UserStatus.Banned, null, null, created, updated);
        var session = new Session(2, 1, "token", "198.51.100.7", "agent", created, created, updated);
        var decoded = RecordCodec.DecodeSession(RecordCodec.EncodeSession(session, user)).User;
        Assert.NotSame(user, decoded);
        Assert.Equal(user, decoded);
        Assert.Equal(123456, Microseconds(decoded.CreatedAt));
        Assert.Equal(654321, Microseconds(decoded.UpdatedAt));
        Assert.Equal(UserStatus.Banned, decoded.Status);
        Assert.Equal(UserRole.Administrator, decoded.Role);
    }

    [Fact]
    public void Foreign_commit_makes_an_old_epoch_query_fresh_records()
    {
        var version = cache.Version();
        Fetch("user", version, () => [record]);
        Commit();
        var fresh = new Row { Name = "Changed elsewhere" };
        Assert.Same(fresh, Fetch("user", version, () => [fresh])[0]);
        Assert.Same(fresh, Fetch("user", cache.Version(), () => [fresh])[0]);
        Assert.Equal("Changed elsewhere", Fetch("user", cache.Version(), () => throw new InvalidOperationException("fresh snapshot missing"))[0].Name);
    }

    [Fact]
    public void Observer_is_rechecked_after_a_snapshot_lookup()
    {
        var version = cache.Version();
        Fetch("user", version, () => [record]);
        store.OnReadHit = Commit;
        var fresh = new Row { Name = "Committed during lookup" };
        Assert.Same(fresh, Fetch("user", version, () => [fresh])[0]);
    }

    [Fact]
    public void A_commit_while_loading_records_prevents_admission()
    {
        var version = cache.Version();
        var writes = store.Writes;
        Assert.Same(record, Fetch("user", version, () =>
        {
            Commit();
            return [record];
        })[0]);
        Assert.Equal(writes, store.Writes);
        var replacement = new Row { Name = "Fresh load" };
        Assert.Same(replacement, Fetch("user", version, () => [replacement])[0]);
    }

    [Fact]
    public void Disabled_budget_missing_epoch_and_open_transactions_always_query()
    {
        var version = cache.Version();
        Fetch("user", version, () => [record]);
        var writes = store.Writes;
        cache.BudgetOverride = 0;
        Assert.Same(record, Fetch("user", version, () => [record])[0]);
        cache.BudgetOverride = 4096;
        Assert.Same(record, Fetch("user", null, () => [record])[0]);
        Assert.Same(record, Fetch("user", version, () => [record], transactionOpen: true)[0]);
        FragmentStore.TransactionOpen = true;
        try
        {
            Assert.Same(record, Fetch("transaction-only", version, () => [record])[0]);
        }
        finally
        {
            FragmentStore.TransactionOpen = false;
        }
        Assert.Equal(writes, store.Writes);
    }

    public void Dispose()
    {
        cache.Clear();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    Row[] Fetch(string key, CacheVersion? version, Func<Row[]> load, bool transactionOpen = false) =>
        RecordCache.Fetch(store, cache, transactionOpen, key, version, load, static row => row is not null, Encode, Decode);

    static byte[] Encode(Row[] rows) => Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(row => row.Name)));

    static Row[] Decode(byte[] bytes) =>
        [.. Encoding.UTF8.GetString(bytes).Split('\n').Select(name => new Row { Name = name })];

    void Commit()
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO changes_for_test VALUES ('committed')";
        command.ExecuteNonQuery();
    }

    static int Microseconds(DateTimeOffset time) => (int)(time.Ticks % TimeSpan.TicksPerSecond / 10);

    sealed class Row
    {
        public string Name { get; set; } = "";
    }
}
