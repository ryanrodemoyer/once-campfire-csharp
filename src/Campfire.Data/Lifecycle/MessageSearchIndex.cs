using Campfire.Data.Sqlite;

namespace Campfire.Data.Lifecycle;

// The `message_search_index` rows message/searchable.rb keeps, from its `after_*_commit`
// callbacks. The body is the message's `plain_text_body`, which the caller renders (rich text's
// `to_plain_text`, else the attachment's filename, else ""). Searching is D04's (Search/).
static class MessageSearchIndex
{
    // `create_in_index`
    public static void Create(SqliteSession session, long messageId, string plainTextBody) =>
        session.Execute("insert into message_search_index(rowid, body) values (@rowid, @body)", ("@rowid", messageId), ("@body", plainTextBody));

    // `update_in_index`
    public static void Update(SqliteSession session, long messageId, string plainTextBody) =>
        session.Execute("update message_search_index set body = @body where rowid = @rowid", ("@body", plainTextBody), ("@rowid", messageId));

    // `remove_from_index`
    public static void Remove(SqliteSession session, long messageId) =>
        session.Execute("delete from message_search_index where rowid = @rowid", ("@rowid", messageId));
}
