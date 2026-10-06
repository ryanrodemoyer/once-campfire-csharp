using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Queries;

// Shared plumbing for the query classes.
static class Sql
{
    public static T? One<T>(SqliteSession session, string sql, Func<SqliteDataReader, T> map, params ReadOnlySpan<(string Name, object? Value)> parameters)
        where T : class
    {
        using var reader = session.Command(sql, parameters).ExecuteReader();
        return reader.Read() ? map(reader) : null;
    }

    public static bool Exists(SqliteSession session, string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        using var reader = session.Command(sql, parameters).ExecuteReader();
        return reader.Read();
    }

    public static long Count(SqliteSession session, string sql, params ReadOnlySpan<(string Name, object? Value)> parameters) =>
        session.Scalar<long>(sql, parameters);

    public static List<long> Ids(SqliteSession session, string sql, params ReadOnlySpan<(string Name, object? Value)> parameters) =>
        session.Query(sql, reader => reader.GetInt64(0), parameters);

    // `INSERT ... RETURNING "id"`
    public static long Insert(SqliteSession session, string sql, params ReadOnlySpan<(string Name, object? Value)> parameters) =>
        session.Scalar<long>(sql, parameters);

    // `@prefix0, @prefix1, ...` for an `IN (...)` list, with the parameters to bind. Rails writes
    // `where(column: [])` as `1=0`; callers check for an empty list first.
    public static (string Placeholders, (string Name, object? Value)[] Parameters) List<T>(string prefix, IReadOnlyList<T> values)
    {
        var parameters = new (string Name, object? Value)[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = ($"@{prefix}{i}", values[i]);
        }
        return (string.Join(", ", parameters.Select(parameter => parameter.Name)), parameters);
    }

    // `update!` of the attributes that changed: nothing is written when none did, and
    // `updated_at` is set with them when some did (Active Record's partial updates).
    public static bool UpdateChanged(SqliteSession session, string table, long id, IReadOnlyList<(string Column, object? Value)> changes, DateTimeOffset now)
    {
        if (changes.Count == 0)
        {
            return false;
        }
        var assignments = changes.Select((change, i) => $"\"{change.Column}\" = @v{i}");
        var sql = $"UPDATE \"{table}\" SET {string.Join(", ", assignments)}, \"updated_at\" = @updated_at WHERE \"{table}\".\"id\" = @id";
        var parameters = changes.Select((change, i) => ($"@v{i}", change.Value))
            .Append(("@updated_at", (object?)Db.Time(now)))
            .Append(("@id", (object?)id))
            .ToArray();
        session.Execute(sql, parameters);
        return true;
    }

    // `touch`: `updated_at` (and any `columns`) set to now.
    public static void Touch(SqliteSession session, string table, long id, DateTimeOffset now)
    {
        session.Execute($"UPDATE \"{table}\" SET \"updated_at\" = @now WHERE \"{table}\".\"id\" = @id", ("@now", Db.Time(now)), ("@id", id));
    }

    public static void Delete(SqliteSession session, string table, long id)
    {
        session.Execute($"DELETE FROM \"{table}\" WHERE \"{table}\".\"id\" = @id", ("@id", id));
    }
}
