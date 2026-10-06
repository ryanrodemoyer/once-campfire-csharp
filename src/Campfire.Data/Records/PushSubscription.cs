using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `push_subscriptions` (reference/app/models/push/subscription.rb, table name from
// `Push.table_name_prefix`): a browser's Web Push endpoint and keys.
public sealed record PushSubscription(
    long Id,
    long UserId,
    string? Endpoint,
    string? P256dhKey,
    string? AuthKey,
    string? UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const string ModelName = "Push::Subscription";

    internal const string Columns = """
        "push_subscriptions"."id", "push_subscriptions"."user_id", "push_subscriptions"."endpoint", "push_subscriptions"."p256dh_key", "push_subscriptions"."auth_key", "push_subscriptions"."user_agent", "push_subscriptions"."created_at", "push_subscriptions"."updated_at"
        """;

    internal static PushSubscription Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static PushSubscription ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        Db.ReadNullableString(reader, at + 2),
        Db.ReadNullableString(reader, at + 3),
        Db.ReadNullableString(reader, at + 4),
        Db.ReadNullableString(reader, at + 5),
        Db.ReadTime(reader, at + 6),
        Db.ReadTime(reader, at + 7));
}
