using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `messages` (reference/app/models/message.rb). The body is the `body` rich text row
// and the file the `attachment` attachment, both keyed by `record_type` "Message".
public sealed record Message(
    long Id,
    long RoomId,
    long CreatorId,
    string ClientMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const string ModelName = "Message";

    internal const string Columns = """
        "messages"."id", "messages"."room_id", "messages"."creator_id", "messages"."client_message_id", "messages"."created_at", "messages"."updated_at"
        """;

    // `before_create -> { self.client_message_id ||= Random.uuid }`
    public static string GenerateClientMessageId() => SecureTokens.Uuid();

    internal static Message Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Message ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetInt64(at + 2),
        reader.GetString(at + 3),
        Db.ReadTime(reader, at + 4),
        Db.ReadTime(reader, at + 5));
}
