using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `sessions` (reference/app/models/session.rb): a signed-in browser, found by the
// `session_token` cookie.
public sealed record Session(
    long Id,
    long UserId,
    string Token,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset LastActiveAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // `ACTIVITY_REFRESH_RATE`
    public static readonly TimeSpan ActivityRefreshRate = TimeSpan.FromHours(1);

    // `has_secure_token`: `SecureRandom.base58(24)`
    public const int TokenLength = 24;

    internal const string Columns = """
        "sessions"."id", "sessions"."user_id", "sessions"."token", "sessions"."ip_address", "sessions"."user_agent", "sessions"."last_active_at", "sessions"."created_at", "sessions"."updated_at"
        """;

    public static string GenerateToken() => SecureTokens.Base58(TokenLength);

    // `resume` refreshes the session when `last_active_at.before?(ACTIVITY_REFRESH_RATE.ago)`.
    public bool NeedsActivityRefresh(DateTimeOffset now) => LastActiveAt < now - ActivityRefreshRate;

    internal static Session Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Session ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetString(at + 2),
        Db.ReadNullableString(reader, at + 3),
        Db.ReadNullableString(reader, at + 4),
        Db.ReadTime(reader, at + 5),
        Db.ReadTime(reader, at + 6),
        Db.ReadTime(reader, at + 7));
}
