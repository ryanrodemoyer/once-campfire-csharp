using Microsoft.Data.Sqlite;

namespace Campfire.Data.Sqlite;

// One connection and its prepared statements. Reads get one from the reader pool and writes get
// the writer's; either way only one thread uses it at a time.
//
// Parameters are named (`@id`, `$id` or `:id`): Microsoft.Data.Sqlite cannot bind anonymous `?`.
public class SqliteSession : IDisposable
{
    readonly StatementCache statements;

    internal SqliteSession(SqliteConnection connection, int statementCacheCapacity)
    {
        Connection = connection;
        statements = new StatementCache(connection, statementCacheCapacity);
    }

    public SqliteConnection Connection { get; }

    internal int CachedStatementCount => statements.Count;

    // The cached, prepared command for `sql` with `parameters` bound. Owned by the session: don't
    // dispose it, and finish with its reader before using the same SQL again.
    public SqliteCommand Command(string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        var command = statements.Get(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    public int Execute(string sql, params ReadOnlySpan<(string Name, object? Value)> parameters) =>
        Command(sql, parameters).ExecuteNonQuery();

    // The first column of the first row, or default when there is no row or it is NULL.
    public T? Scalar<T>(string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        using var reader = Command(sql, parameters).ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetFieldValue<T>(0) : default;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        var rows = new List<T>();
        using var reader = Command(sql, parameters).ExecuteReader();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            statements.Dispose();
            Connection.Dispose();
        }
    }
}
