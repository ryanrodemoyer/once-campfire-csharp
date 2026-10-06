using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Pagination;

// reference/app/models/message/pagination.rb, on `room.messages`. Pages are by `created_at`,
// oldest first. The SQL is what Active Record sends: `last` reverses `ordered` to DESC and flips
// the rows back, so SQLite breaks `created_at` ties the same way. Times are bound as
// `Quoting#quoted_date` writes them (Db.Time), not as the column's stored text.
public static class MessagePages
{
    public const int PageSize = 40;
    const string pageSizeText = "40";

    const string inRoom = $"SELECT {Message.Columns} FROM \"messages\" WHERE \"messages\".\"room_id\" = @room_id";
    const string ascending = $"ORDER BY \"messages\".\"created_at\" ASC LIMIT {pageSizeText}";
    const string descending = $"ORDER BY \"messages\".\"created_at\" DESC LIMIT {pageSizeText}";

    // `last_page`: the newest page.
    public static List<Message> LastPage(SqliteSession session, long roomId) =>
        Last(session, $"{inRoom} {descending}", ("@room_id", roomId));

    // `first_page`: the oldest page.
    public static List<Message> FirstPage(SqliteSession session, long roomId) =>
        session.Query($"{inRoom} {ascending}", Message.Read, ("@room_id", roomId));

    // `page_before(message)`: `before(message).last_page`, the page that ends just before it.
    public static List<Message> PageBefore(SqliteSession session, long roomId, Message message) =>
        Last(session, $"{inRoom} AND (created_at < @time) {descending}", ("@room_id", roomId), ("@time", Db.Time(message.CreatedAt)));

    // `page_after(message)`: `after(message).first_page`, the page that starts just after it.
    public static List<Message> PageAfter(SqliteSession session, long roomId, Message message) =>
        session.Query($"{inRoom} AND (created_at > @time) {ascending}", Message.Read, ("@room_id", roomId), ("@time", Db.Time(message.CreatedAt)));

    // `page_around(message)`: up to a page before it, the message, and up to a page after it.
    public static List<Message> PageAround(SqliteSession session, long roomId, Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var page = PageBefore(session, roomId, message);
        page.Add(message);
        page.AddRange(PageAfter(session, roomId, message));
        return page;
    }

    // `page_created_since(time)`: the oldest page created after `time` (Rooms::RefreshesController).
    public static List<Message> PageCreatedSince(SqliteSession session, long roomId, DateTimeOffset time) =>
        session.Query($"{inRoom} AND (created_at > @time) {ascending}", Message.Read, ("@room_id", roomId), ("@time", Db.Time(time)));

    // `without(excluding).page_updated_since(time)`: the newest page of messages updated after
    // `time`, leaving out `excluding` (the refresh's new messages).
    public static List<Message> PageUpdatedSince(SqliteSession session, long roomId, DateTimeOffset time, IReadOnlyList<long> excluding)
    {
        ArgumentNullException.ThrowIfNull(excluding);
        var (placeholders, parameters) = Sql.List("id", excluding);
        var without = excluding.Count == 0 ? "" : $" AND \"messages\".\"id\" NOT IN ({placeholders})";
        return Last(session, $"{inRoom}{without} AND (updated_at > @time) {descending}",
            [("@room_id", roomId), .. parameters, ("@time", Db.Time(time))]);
    }

    // `before(message).exists?` (Messages::ByBotsController's previous-page link).
    public static bool ExistsBefore(SqliteSession session, long roomId, Message message) =>
        Sql.Exists(session, "SELECT 1 FROM \"messages\" WHERE \"messages\".\"room_id\" = @room_id AND (created_at < @time) LIMIT 1", ("@room_id", roomId), ("@time", Db.Time(message.CreatedAt)));

    // `after(message).exists?` (Messages::ByBotsController's next-page link).
    public static bool ExistsAfter(SqliteSession session, long roomId, Message message) =>
        Sql.Exists(session, "SELECT 1 FROM \"messages\" WHERE \"messages\".\"room_id\" = @room_id AND (created_at > @time) LIMIT 1", ("@room_id", roomId), ("@time", Db.Time(message.CreatedAt)));

    // `paged?`: more than a page (`count > PAGE_SIZE`), asked as whether a row exists past the
    // first page so a long room isn't counted in full.
    public static bool IsPaged(SqliteSession session, long roomId) =>
        Sql.Exists(session, $"SELECT 1 FROM \"messages\" WHERE \"messages\".\"room_id\" = @room_id LIMIT 1 OFFSET {pageSizeText}", ("@room_id", roomId));

    // `relation.last(n)`: the DESC query's rows, flipped back to oldest first.
    static List<Message> Last(SqliteSession session, string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        var page = session.Query(sql, Message.Read, parameters);
        page.Reverse();
        return page;
    }
}
