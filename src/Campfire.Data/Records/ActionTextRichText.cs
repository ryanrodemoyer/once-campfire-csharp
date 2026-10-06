using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `action_text_rich_texts`: the HTML of a record's rich text attribute (messages'
// `body`), keyed by record_type, record_id and name.
public sealed record ActionTextRichText(
    long Id,
    string Name,
    string? Body,
    string RecordType,
    long RecordId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const string ModelName = "ActionText::RichText";

    internal const string Columns = """
        "action_text_rich_texts"."id", "action_text_rich_texts"."name", "action_text_rich_texts"."body", "action_text_rich_texts"."record_type", "action_text_rich_texts"."record_id", "action_text_rich_texts"."created_at", "action_text_rich_texts"."updated_at"
        """;

    internal static ActionTextRichText Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static ActionTextRichText ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetString(at + 1),
        Db.ReadNullableString(reader, at + 2),
        reader.GetString(at + 3),
        reader.GetInt64(at + 4),
        Db.ReadTime(reader, at + 5),
        Db.ReadTime(reader, at + 6));
}
