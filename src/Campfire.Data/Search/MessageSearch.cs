using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Searching;

// Message.search (reference/app/models/message/searchable.rb) over `user.reachable_messages`, as
// SearchesController#set_messages runs it: the FTS5 `message_search_index` (kept by
// Lifecycle/MessageSearchIndex from each message's plain text) matched against the sanitized
// query, in the rooms the user is a member of. The SQL is what Active Record sends; `last_page_of`
// reverses `ordered` to DESC and flips the rows back, so SQLite breaks `created_at` ties the same
// way. A query FTS5 can't parse (say a bare "AND") throws its SqliteException
// ("fts5: syntax error near ..."), as Rails raises ActiveRecord::StatementInvalid.
public static class MessageSearch
{
    // `last_page_of(100)`
    public const int Limit = 100;
    const string limitText = "100";

    const string reachable = $"""
        SELECT {Message.Columns} FROM "messages" INNER JOIN "rooms" ON "messages"."room_id" = "rooms"."id" INNER JOIN "memberships" ON "rooms"."id" = "memberships"."room_id" join message_search_index idx on messages.id = idx.rowid WHERE "memberships"."user_id" = @user_id AND (idx.body match @query) ORDER BY "messages"."created_at" DESC LIMIT {limitText}
        """;

    // `Current.user.reachable_messages.search(query).last_page_of(100)`: up to the newest hundred
    // matches, oldest first. `query` is SearchQuery.Searchable's.
    public static List<Message> Reachable(SqliteSession session, long userId, string query)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);
        var messages = session.Query(reachable, Message.Read, ("@user_id", userId), ("@query", query));
        messages.Reverse();
        return messages;
    }
}
