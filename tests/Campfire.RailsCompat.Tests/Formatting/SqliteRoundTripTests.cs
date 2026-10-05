using Campfire.RailsCompat.Formatting;
using Microsoft.Data.Sqlite;

namespace Campfire.RailsCompat.Tests.Formatting;

// Rails -> SQLite -> C# -> SQLite -> Rails: the text Rails wrote is read by C#, written back by C#,
// and is the same text, which Rails reads as the same time (DatetimeTextReadsAsActiveRecordReadsIt).
public sealed class SqliteRoundTripTests
{
    [Fact]
    public void TimestampsSurviveARoundTripThroughSqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE messages (id INTEGER PRIMARY KEY, created_at datetime(6) NOT NULL)");

        var railsTexts = FormattingVectors.File.Times.Select(t => t.Db)
            // What insert_all stamps rows with: STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW').
            .Append("2026-09-26 12:25:26.826")
            .ToList();
        foreach (var text in railsTexts)
        {
            Execute(connection, "INSERT INTO messages (created_at) VALUES (@at)", text);
        }
        Execute(connection, "INSERT INTO messages (created_at) VALUES (STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW'))");
        Execute(connection, "INSERT INTO messages (created_at) VALUES (CURRENT_TIMESTAMP)");

        var rows = ReadAll(connection);
        foreach (var (id, text) in rows)
        {
            var time = ActiveRecordTime.FromDb(text);
            Assert.NotNull(time);
            Execute(connection, "UPDATE messages SET created_at = @at WHERE id = " + id, ActiveRecordTime.ToDb(time.Value));
        }

        var written = ReadAll(connection);
        for (var i = 0; i < railsTexts.Count; i++)
        {
            var expected = railsTexts[i] == "2026-09-26 12:25:26.826" ? "2026-09-26 12:25:26.826000" : railsTexts[i];
            Assert.Equal(expected, written[i].Text);
        }
        // SQLite's own stamps come back in Rails' spelling of the same instant.
        foreach (var (before, after) in rows.Zip(written).Skip(railsTexts.Count))
        {
            Assert.Equal(ActiveRecordTime.FromDb(before.Text), ActiveRecordTime.FromDb(after.Text));
        }
    }

    static void Execute(SqliteConnection connection, string sql, string? at = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (at is not null)
        {
            command.Parameters.AddWithValue("@at", at);
        }
        command.ExecuteNonQuery();
    }

    static List<(long Id, string Text)> ReadAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, created_at FROM messages ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<(long, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return rows;
    }
}
