using System.Globalization;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Schema;

public enum PrepareResult
{
    // The database had no schema_migrations table, so the schema was loaded.
    Loaded,

    // Every migration had already run.
    UpToDate,
}

public sealed class PendingMigrationsException(IReadOnlyList<string> versions)
    : InvalidOperationException(
        $"The database is missing migrations {string.Join(", ", versions)}. " +
        "Boot the Rails app on it once to migrate it, then start Campfire again.")
{
    public IReadOnlyList<string> Versions { get; } = versions;
}

// `bin/rails db:prepare` for one database (DatabaseTasks#initialize_database, then #migrate).
// A database without a schema_migrations table gets the schema loaded, the migrations marked as
// run and ar_internal_metadata written, as `load_schema` does. A database that has one is left
// alone if every migration has run. Migrations aren't ported, so where Rails would run the
// missing ones this refuses the database instead.
public static class SchemaPreparer
{
    public static PrepareResult Prepare(SqliteSession session, string environment, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);

        if (TableExists(session, "schema_migrations"))
        {
            var pending = PendingMigrations(session);
            if (pending.Count > 0)
            {
                throw new PendingMigrationsException(pending);
            }
            return PrepareResult.UpToDate;
        }

        WriteTransaction.Run(session, transaction =>
        {
            LoadSchema(transaction.Session, environment, clock);
            return true;
        });
        return PrepareResult.Loaded;
    }

    public static List<string> PendingMigrations(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return RailsSchema.MigrationVersions
            .Where(version => session.Scalar<long>("SELECT 1 FROM schema_migrations WHERE version = @version", ("@version", version)) == 0)
            .ToList();
    }

    static void LoadSchema(SqliteSession session, string environment, TimeProvider clock)
    {
        foreach (var statement in RailsSchema.Statements)
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        // ConnectionAdapters::SchemaStatements#assume_migrated_upto_version inserts the schema's
        // version first, then the other migrations, newest first.
        foreach (var version in RailsSchema.MigrationVersions.Reverse())
        {
            session.Execute("""INSERT INTO "schema_migrations" ("version") VALUES (@version)""", ("@version", version));
        }

        // InternalMetadata#create_table_and_set_flags
        SetInternalMetadata(session, "environment", environment, clock.GetUtcNow());
        SetInternalMetadata(session, "schema_sha1", RailsSchema.SchemaSha1, clock.GetUtcNow());
    }

    // InternalMetadata#update_or_create_entry
    static void SetInternalMetadata(SqliteSession session, string key, string value, DateTimeOffset now)
    {
        var current = session.Query(
            """SELECT "value" FROM "ar_internal_metadata" WHERE "key" = @key""",
            reader => reader.IsDBNull(0) ? null : reader.GetString(0),
            ("@key", key));
        var timestamp = QuotedDate(now);
        if (current.Count == 0)
        {
            session.Execute(
                """INSERT INTO "ar_internal_metadata" ("key", "value", "created_at", "updated_at") VALUES (@key, @value, @now, @now)""",
                ("@key", key), ("@value", value), ("@now", timestamp));
        }
        else if (current[0] != value)
        {
            session.Execute(
                """UPDATE "ar_internal_metadata" SET "value" = @value, "updated_at" = @now WHERE "key" = @key""",
                ("@key", key), ("@value", value), ("@now", timestamp));
        }
    }

    // Quoting#quoted_date: UTC to the second, then microseconds only when there are any.
    static string QuotedDate(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        var microseconds = utc.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMicrosecond;
        var seconds = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return microseconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"{seconds}.{microseconds:D6}") : seconds;
    }

    static bool TableExists(SqliteSession session, string name) =>
        session.Scalar<long>("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name", ("@name", name)) == 1;
}
