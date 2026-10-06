using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `searches` (reference/app/models/search.rb): one of a user's recent searches.
public sealed record Search(
    long Id,
    long UserId,
    string Query,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // `trim_recent_searches` keeps this many.
    public const int RecentLimit = 10;

    internal const string Columns = """
        "searches"."id", "searches"."user_id", "searches"."query", "searches"."created_at", "searches"."updated_at"
        """;

    internal static Search Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Search ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetString(at + 2),
        Db.ReadTime(reader, at + 3),
        Db.ReadTime(reader, at + 4));
}
