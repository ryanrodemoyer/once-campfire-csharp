using System.Text;
using Campfire.Web.ResponseCache;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary>
/// <c>reference/test/models/response_cache_test.rb</c>. A foreign commit expires entries, the byte
/// budget evicts and rejects, and cold renders of one page share a lock without holding the observer.
/// </summary>
public sealed class ResponseCacheTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-response-cache", Guid.NewGuid().ToString("N"));
    readonly string database;
    readonly Campfire.Web.ResponseCache.ResponseCache cache;

    public ResponseCacheTests()
    {
        Directory.CreateDirectory(directory);
        database = CreateDatabase("cache.sqlite3", "WAL");
        cache = new Campfire.Web.ResponseCache.ResponseCache(() => database) { BudgetOverride = 1024 };
    }

    [Fact]
    public void A_wal_database_is_observed_through_its_wal_index_header()
    {
        Assert.NotNull(cache.Version());
        Assert.True(cache.ObservesWalIndex);
    }

    // Each commit rewrites the WAL-index header, so each one moves the version, as data_version does.
    // A rollback-journal database has no WAL index and observes data_version on pooled connections.
    [Theory]
    [InlineData("WAL")]
    [InlineData("DELETE")]
    public void Every_foreign_commit_moves_the_version(string journalMode)
    {
        var path = CreateDatabase($"every-commit-{journalMode}.sqlite3", journalMode);
        var observed = new Campfire.Web.ResponseCache.ResponseCache(() => path) { BudgetOverride = 1024 };
        var version = observed.Version();
        Assert.Equal(journalMode == "WAL", observed.ObservesWalIndex);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(version, observed.Version());
            Commit(path, "INSERT INTO values_for_test VALUES ('committed')");
            var next = observed.Version();
            Assert.NotEqual(version, next);
            version = next;
        }
        observed.Clear();
    }

    [Fact]
    public void Foreign_commits_expire_entries_and_reject_old_render_admission()
    {
        var version = cache.Version();
        cache.Write("page", version, Entry("first"));
        Assert.Equal("first", Body(cache.Read("page", version)));
        Commit("INSERT INTO values_for_test VALUES ('committed')");
        Assert.Null(cache.Read("page", version));
        cache.Write("page", version, Entry("old in-flight render"));
        Assert.Null(cache.Read("page", cache.Version()));
        cache.Write("page", cache.Version(), Entry("fresh"));
        Assert.Equal("fresh", Body(cache.Read("page", cache.Version())));
    }

    // Without a WAL index, concurrent callers each observe data_version on their own pooled
    // connection. However many observers there are and whichever one a caller gets, a commit
    // expires what came before it.
    [Fact]
    public async Task Every_pooled_observer_sees_a_foreign_commit()
    {
        UseRollbackJournal();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.Version(), TestContext.Current.CancellationToken)));
        var before = cache.Version();
        cache.Write("page", before, Entry("before"));

        Commit("INSERT INTO values_for_test VALUES ('committed')");

        var after = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.Version(), TestContext.Current.CancellationToken)));
        Assert.All(after, version => Assert.NotEqual(before, version));
        Assert.Null(cache.Read("page", cache.Version()));
    }

    // data_version can't be compared across connections, so an observer opened after a commit
    // must not take that commit's data_version as already seen.
    [Fact]
    public async Task A_new_observer_does_not_hide_a_commit_before_it_opened()
    {
        UseRollbackJournal();
        var version = cache.Version();
        cache.Write("page", version, Entry("before"));

        Commit("INSERT INTO values_for_test VALUES ('committed')");
        // Two at once: at least one of them opens a new observer.
        var versions = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => cache.Version(), TestContext.Current.CancellationToken)));

        Assert.All(versions, current => Assert.NotEqual(version, current));
        Assert.Null(cache.Read("page", version));
    }

    [Fact]
    public void Byte_budget_evicts_oldest_and_bypasses_oversized_entries_and_keys()
    {
        var version = cache.Version();
        cache.BudgetOverride = 512;
        cache.Write("first", version, Entry(new string('a', 100)));
        cache.Write("second", version, Entry(new string('b', 100)));
        Assert.Null(cache.Read("first", version));
        Assert.Equal(new string('b', 100), Body(cache.Read("second", version)));
        cache.Write("oversized", version, Entry(new string('c', 1000)));
        Assert.Null(cache.Read("oversized", version));
        cache.Write(new string('k', 2049), version, Entry("small"));
        Assert.Null(cache.Read(new string('k', 2049), version));
    }

    [Fact]
    public void Clearing_the_observer_gives_later_pages_a_distinct_namespace()
    {
        var version = cache.Version();
        cache.Clear();
        Assert.NotEqual(version, cache.Version());
    }

    [Fact]
    public void Cold_renders_coalesce_without_holding_the_observer_mutex()
    {
        var version = cache.Version()!;
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var first = new Thread(() =>
        {
            cache.SynchronizeRender("page", version, () =>
            {
                entered.Set();
                release.Wait();
                cache.Write("page", version, Entry("completed render"));
            });
        });
        first.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        CachedPage? secondPage = null;
        var secondStarted = new ManualResetEventSlim();
        var secondEntered = new ManualResetEventSlim();
        var second = new Thread(() =>
        {
            secondStarted.Set();
            cache.SynchronizeRender("page", version, () =>
            {
                secondEntered.Set();
                secondPage = cache.Read("page", version);
            });
        });
        second.Start();
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        // The second render is blocked on the shared lock: it has not entered, and it has not finished.
        Assert.False(second.Join(TimeSpan.FromMilliseconds(200)));
        Assert.False(secondEntered.IsSet);
        Assert.Null(cache.Read("page", version));
        Assert.Equal(version, cache.Version());
        release.Set();
        Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(first.Join(TimeSpan.FromSeconds(2)));
        Assert.True(second.Join(TimeSpan.FromSeconds(2)));
        Assert.Equal("completed render", Body(secondPage));
    }

    public void Dispose()
    {
        cache.Clear();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    void Commit(string sql) => Commit(database, sql);

    static void Commit(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    string CreateDatabase(string name, string journalMode)
    {
        var path = Path.Combine(directory, name);
        Commit(path, $"""
            PRAGMA journal_mode={journalMode};
            CREATE TABLE values_for_test (value TEXT);
            """);
        return path;
    }

    // Switch the fixture's database out of WAL mode, so the cache observes it through pooled connections.
    void UseRollbackJournal()
    {
        cache.Clear();
        SqliteConnection.ClearAllPools();
        Commit("PRAGMA journal_mode=DELETE");
    }

    static CachedPage Entry(string body) => new(Encoding.UTF8.GetBytes(body), []);

    static string? Body(CachedPage? page) => page is null ? null : Encoding.UTF8.GetString(page.Body);
}
