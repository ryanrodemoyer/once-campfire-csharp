using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/message.rb. Pagination is D05's (Pagination/) and search D04's (Search/).
public static class Messages
{
    const string select = $"SELECT {Message.Columns} FROM \"messages\"";

    // `scope :ordered, -> { order(:created_at) }`
    const string ordered = "ORDER BY \"messages\".\"created_at\" ASC";

    public static Message? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"messages\".\"id\" = @id LIMIT 1", Message.Read, ("@id", id));

    // `room.messages.find_by(id:)`
    public static Message? FindInRoom(SqliteSession session, long roomId, long id) =>
        Sql.One(session, $"{select} WHERE \"messages\".\"room_id\" = @room_id AND \"messages\".\"id\" = @id LIMIT 1", Message.Read, ("@room_id", roomId), ("@id", id));

    // `Message.where(id: ids)`, in the table's order.
    public static List<Message> WhereIds(SqliteSession session, IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("id", ids);
        return session.Query($"{select} WHERE \"messages\".\"id\" IN ({placeholders})", Message.Read, parameters);
    }

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"messages\"");

    // `room.messages.count`
    public static long CountInRoom(SqliteSession session, long roomId) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"messages\" WHERE \"messages\".\"room_id\" = @room_id", ("@room_id", roomId));

    // `room.messages.ordered`
    public static List<Message> InRoomOrdered(SqliteSession session, long roomId) =>
        session.Query($"{select} WHERE \"messages\".\"room_id\" = @room_id {ordered}", Message.Read, ("@room_id", roomId));

    // `room.messages`, as `room.destroy` destroys them.
    public static List<Message> InRoom(SqliteSession session, long roomId) =>
        session.Query($"{select} WHERE \"messages\".\"room_id\" = @room_id", Message.Read, ("@room_id", roomId));

    // `user.messages`, as `remove_banned_content` destroys them (user/bannable.rb).
    public static List<Message> ByCreator(SqliteSession session, long creatorId) =>
        session.Query($"{select} WHERE \"messages\".\"creator_id\" = @creator_id", Message.Read, ("@creator_id", creatorId));

    // The message row of `room.messages.create!(creator:, client_message_id:)`. A missing client
    // message id gets a UUID (`before_create`).
    public static Message Create(SqliteSession session, long roomId, long creatorId, string? clientMessageId, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "messages" ("client_message_id", "created_at", "creator_id", "room_id", "updated_at") VALUES (@client_message_id, @now, @creator_id, @room_id, @now) RETURNING "id"
            """, ("@client_message_id", clientMessageId ?? Message.GenerateClientMessageId()), ("@now", Db.Time(now)), ("@creator_id", creatorId), ("@room_id", roomId));
        return Find(session, id)!;
    }

    // `touch`, from `belongs_to :message, touch: true` on boosts and from body edits.
    public static void Touch(SqliteSession session, long messageId, DateTimeOffset now) => Sql.Touch(session, "messages", messageId, now);

    // The message's own row. `message.destroy` destroys its boosts, rich text and attachment.
    public static void Delete(SqliteSession session, long messageId) => Sql.Delete(session, "messages", messageId);
}
