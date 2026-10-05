using Microsoft.Data.Sqlite;

namespace Campfire.Data.Sqlite;

// Opens connections configured the way Rails configures its own: `timeout: 5000` from
// reference/config/database.yml, then the sqlite3 adapter's DEFAULT_PRAGMAS, in the adapter's
// order (SQLite3Adapter#configure_connection in activerecord/lib/active_record/
// connection_adapters/sqlite3_adapter.rb at the reference's pinned Rails revision).
public static class SqliteConnections
{
    // `timeout: 5000`
    public const int BusyTimeoutMilliseconds = 5000;

    // SQLite3Adapter::DEFAULT_PRAGMAS
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Pragmas =
    [
        new("foreign_keys", "ON"),
        new("journal_mode", "wal"),
        new("synchronous", "normal"),
        new("mmap_size", "134217728"),
        new("journal_size_limit", "67108864"),
        new("cache_size", "2000"),
    ];

    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // Connections live for the life of the database handle, so ADO.NET pooling would only
            // hide them. Microsoft.Data.Sqlite also retries SQLITE_BUSY until the command timeout,
            // which is kept at the busy timeout so that a lock is waited on for 5s, as in Rails.
            Pooling = false,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            Configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    static void Configure(SqliteConnection connection)
    {
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}");
        foreach (var (pragma, value) in Pragmas)
        {
            Execute(connection, $"PRAGMA {pragma} = {value}");
        }
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        // A pragma that reports its new value (journal_mode, mmap_size) returns a row.
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
        }
    }
}
