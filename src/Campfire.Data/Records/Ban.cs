using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `bans` (reference/app/models/ban.rb): an IP address a banned user signed in from.
public sealed record Ban(
    long Id,
    long UserId,
    string IpAddress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal const string Columns = """
        "bans"."id", "bans"."user_id", "bans"."ip_address", "bans"."created_at", "bans"."updated_at"
        """;

    internal static Ban Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Ban ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetString(at + 2),
        Db.ReadTime(reader, at + 3),
        Db.ReadTime(reader, at + 4));
}
