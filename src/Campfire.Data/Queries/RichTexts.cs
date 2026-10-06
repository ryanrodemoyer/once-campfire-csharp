using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// `action_text_rich_texts` rows: a record's `has_rich_text` attributes (messages' `body`).
public static class RichTexts
{
    const string select = $"SELECT {ActionTextRichText.Columns} FROM \"action_text_rich_texts\"";

    public static ActionTextRichText? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"action_text_rich_texts\".\"id\" = @id LIMIT 1", ActionTextRichText.Read, ("@id", id));

    // `message.body`: `has_one :rich_text_body`.
    public static ActionTextRichText? For(SqliteSession session, string recordType, long recordId, string name) =>
        Sql.One(session,
            $"{select} WHERE \"action_text_rich_texts\".\"record_id\" = @record_id AND \"action_text_rich_texts\".\"record_type\" = @record_type AND \"action_text_rich_texts\".\"name\" = @name LIMIT 1",
            ActionTextRichText.Read,
            ("@record_id", recordId), ("@record_type", recordType), ("@name", name));

    // The rich texts of several records, as `with_rich_text_body` preloads them.
    public static List<ActionTextRichText> ForRecords(SqliteSession session, string recordType, string name, IReadOnlyList<long> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("record_id", recordIds);
        return session.Query(
            $"{select} WHERE \"action_text_rich_texts\".\"record_type\" = @record_type AND \"action_text_rich_texts\".\"name\" = @name AND \"action_text_rich_texts\".\"record_id\" IN ({placeholders})",
            ActionTextRichText.Read,
            [("@record_type", recordType), ("@name", name), .. parameters]);
    }

    public static ActionTextRichText Create(SqliteSession session, string recordType, long recordId, string name, string? body, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "action_text_rich_texts" ("body", "created_at", "name", "record_id", "record_type", "updated_at") VALUES (@body, @now, @name, @record_id, @record_type, @now) RETURNING "id"
            """, ("@body", body), ("@now", Db.Time(now)), ("@name", name), ("@record_id", recordId), ("@record_type", recordType));
        return Find(session, id)!;
    }

    // `update!(body:)`
    public static ActionTextRichText UpdateBody(SqliteSession session, ActionTextRichText richText, string? body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(richText);
        return Sql.UpdateChanged(session, "action_text_rich_texts", richText.Id, body == richText.Body ? [] : [("body", body)], now)
            ? Find(session, richText.Id)!
            : richText;
    }

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "action_text_rich_texts", id);
}
