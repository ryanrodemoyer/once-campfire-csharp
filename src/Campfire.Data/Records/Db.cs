using Campfire.RailsCompat.Formatting;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// Reading and writing column values the way Active Record does on SQLite. Records select their
// columns by name, in the order of their `Columns` list, and read them back by position.
static class Db
{
    // What `insert_all` stamps `created_at` and `updated_at` with on SQLite
    // (`high_precision_current_timestamp`): the database's clock, to the millisecond.
    public const string SqliteNow = "STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW')";

    // A `datetime(6)` value as Active Record binds it.
    public static string Time(DateTimeOffset time) => ActiveRecordTime.ToDb(time);

    public static string? Time(DateTimeOffset? time) => time is { } value ? ActiveRecordTime.ToDb(value) : null;

    public static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) =>
        ReadNullableTime(reader, ordinal)
        ?? throw new InvalidDataException($"{reader.GetName(ordinal)} holds a time Active Record can't read: {reader.GetValue(ordinal)}");

    public static DateTimeOffset? ReadNullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ActiveRecordTime.FromDb(reader.GetString(ordinal));

    public static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
