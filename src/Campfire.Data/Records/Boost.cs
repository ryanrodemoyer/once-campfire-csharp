using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `boosts` (reference/app/models/boost.rb): a short reaction to a message.
public sealed record Boost(
    long Id,
    long MessageId,
    long BoosterId,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // `t.string "content", limit: 16`
    public const int ContentLimit = 16;

    internal const string Columns = """
        "boosts"."id", "boosts"."message_id", "boosts"."booster_id", "boosts"."content", "boosts"."created_at", "boosts"."updated_at"
        """;

    internal static Boost Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Boost ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetInt64(at + 2),
        reader.GetString(at + 3),
        Db.ReadTime(reader, at + 4),
        Db.ReadTime(reader, at + 5));
}
