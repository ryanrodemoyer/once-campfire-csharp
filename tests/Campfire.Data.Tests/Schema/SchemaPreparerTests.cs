using System.Security.Cryptography;
using Campfire.Data.Schema;
using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Tests.Schema;

// The oracle is a database Active Record prepared at the reference's pinned Rails revision
// (Oracle/generate.rb): rails-schema.sql is its sqlite_master and rails-fixtures.sqlite3 the
// database itself, prepared with the clock at 2026-03-02T16:00:00Z.
public class SchemaPreparerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset OracleTime = new(2026, 3, 2, 16, 0, 0, TimeSpan.Zero);

    static SqliteDatabase Prepare(TestDatabase file, DateTimeOffset? now = null) =>
        SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1, Clock = new FixedClock(now ?? OracleTime) });

    // What `sqlite3 .schema` prints, minus the CLI's rewriting of FTS5 shadow tables to
    // `CREATE TABLE IF NOT EXISTS`: every object's SQL in creation order.
    static string SqliteMaster(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var schema = new System.Text.StringBuilder();
        while (reader.Read())
        {
            schema.Append(reader.GetString(0)).Append(";\n");
        }
        return schema.ToString();
    }

    static List<string[]> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetString(i))]);
        }
        return rows;
    }

    static SqliteConnection OpenRailsDatabase() =>
        new($"Data Source={TestDatabase.Oracle("rails-fixtures.sqlite3")};Mode=ReadOnly;Pooling=False");

    [Fact]
    public async Task Prepared_schema_is_the_one_rails_creates()
    {
        using var file = new TestDatabase();
        using (var database = Prepare(file))
        {
            var schema = await database.ReadAsync(session => SqliteMaster(session.Connection), Ct);
            Assert.Equal(await File.ReadAllTextAsync(TestDatabase.Oracle("rails-schema.sql"), Ct), schema);
        }
    }

    [Fact]
    public async Task Schema_migrations_and_internal_metadata_match_rails()
    {
        using var file = new TestDatabase();
        using var rails = OpenRailsDatabase();
        rails.Open();
        using var database = Prepare(file);

        foreach (var sql in new[]
        {
            "SELECT version FROM schema_migrations ORDER BY rowid",
            "SELECT key, value, created_at, updated_at FROM ar_internal_metadata ORDER BY rowid",
        })
        {
            Assert.Equal(Rows(rails, sql), await database.ReadAsync(session => Rows(session.Connection, sql), Ct));
        }
    }

    [Fact]
    public async Task Internal_metadata_timestamps_keep_microseconds_like_quoted_date()
    {
        using var file = new TestDatabase();
        using var database = Prepare(file, new DateTimeOffset(2026, 10, 5, 17, 5, 24, TimeSpan.Zero).AddTicks(9_382_634));

        var createdAt = await database.ReadAsync(session =>
            session.Scalar<string>("SELECT created_at FROM ar_internal_metadata WHERE key = 'environment'"), Ct);
        Assert.Equal("2026-10-05 17:05:24.938263", createdAt);
    }

    [Fact]
    public void Preparing_again_leaves_the_database_alone()
    {
        using var file = new TestDatabase();
        Prepare(file).Dispose();

        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);
        var before = SqliteMaster(session.Connection);
        Assert.Equal(PrepareResult.UpToDate, SchemaPreparer.Prepare(session, "production", new FixedClock(OracleTime.AddDays(1))));
        Assert.Equal(before, SqliteMaster(session.Connection));
        Assert.Equal(15, session.Scalar<long>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public void Rails_database_is_up_to_date()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);
        Assert.Equal(PrepareResult.UpToDate, SchemaPreparer.Prepare(session, "production", TimeProvider.System));
    }

    [Fact]
    public void Database_with_missing_migrations_is_refused()
    {
        using var file = TestDatabase.FromRailsFixtures();
        using (var session = new SqliteSession(SqliteConnections.Open(file.Path), 10))
        {
            session.Execute("DELETE FROM schema_migrations WHERE version IN ('20251126130131', '20251212154340')");
        }

        var error = Assert.Throws<PendingMigrationsException>(() => SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path)));
        Assert.Equal(["20251126130131", "20251212154340"], error.Versions);
    }

    [Fact]
    public void Migration_versions_are_the_reference_migrations()
    {
        var versions = Directory.GetFiles(TestDatabase.Reference("db/migrate"), "*.rb")
            .Select(path => Path.GetFileName(path).Split('_')[0])
            .Order(StringComparer.Ordinal);
        Assert.Equal(versions, RailsSchema.MigrationVersions);
    }

    [Fact]
    public void Schema_sha1_is_the_reference_schema_rb()
    {
#pragma warning disable CA5350 // Rails records schema.rb's SHA1 (ActiveRecord::Tasks::DatabaseTasks#schema_sha1).
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(TestDatabase.Reference("db/schema.rb"))));
#pragma warning restore CA5350
        Assert.Equal(RailsSchema.SchemaSha1, sha1);
    }

    [Fact]
    public async Task Message_search_index_is_fts5_with_the_porter_stemmer()
    {
        using var file = new TestDatabase();
        using var database = Prepare(file);

        await database.WriteAsync(tx => tx.Session.Execute("INSERT INTO message_search_index (rowid, body) VALUES (7, 'running dogs')"), Ct);
        var hit = await database.ReadAsync(session =>
            session.Scalar<long>("SELECT rowid FROM message_search_index WHERE message_search_index MATCH @query", ("@query", "run")), Ct);
        Assert.Equal(7, hit);
    }
}
