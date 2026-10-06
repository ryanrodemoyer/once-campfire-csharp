using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `webhooks` (reference/app/models/webhook.rb): where a bot's messages are posted. A
// bot has at most one (`has_one :webhook`).
public sealed record Webhook(
    long Id,
    long UserId,
    string? Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // `ENDPOINT_TIMEOUT`
    public static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(7);

    internal const string Columns = """
        "webhooks"."id", "webhooks"."user_id", "webhooks"."url", "webhooks"."created_at", "webhooks"."updated_at"
        """;

    internal static Webhook Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Webhook ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        Db.ReadNullableString(reader, at + 2),
        Db.ReadTime(reader, at + 3),
        Db.ReadTime(reader, at + 4));
}
