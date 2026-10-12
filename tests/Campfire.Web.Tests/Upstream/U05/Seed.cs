using Campfire.Data.Sqlite;
using Campfire.Web.Tests.Controllers.Messages;
using SQLitePCL;

namespace Campfire.Web.Tests.Upstream.U05;

/// <summary>The default parity seed, where U05's ports of the reference tests run, and its ids.</summary>
static class Seed
{
    public const long David = 127326141;
    public const long Jason = 149087659;
    public const long Kevin = 712064548;
    public const long Jz = 773523953;

    public const long Designers = 654632876;
    public const long DavidAndJason = 186869642;
    public const long DavidAndKevin = 699448325;

    public const string DavidSession = "AxJs94fteQ5Autv2VrKsH68c";

    public static MessagesApp App(IEnumerable<string>? fixtures = null) => new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero), fixtures);

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // `room.messages.create!(body:, client_message_id:, creator: users(:david))`, as David posts it.
    public static async Task<long> PostAsync(MessagesApp app, long roomId, string body, string clientMessageId, IReadOnlyDictionary<string, string>? headers = null)
    {
        var form = $"message%5Bbody%5D={Uri.EscapeDataString(body)}&message%5Bclient_message_id%5D={Uri.EscapeDataString(clientMessageId)}";
        var response = await app.SendAsync("POST", $"/rooms/{roomId}/messages.turbo_stream", headers ?? app.SignedIn(DavidSession), form);
        Assert.Equal(200, response.Status);
        return (long)app.Scalar($"SELECT MAX(id) FROM messages WHERE room_id = {roomId}")!;
    }

    public static void Execute(MessagesApp app, string sql)
    {
        using var connection = app.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // `ActiveSupport::Notifications.subscribed(counter, "sql.active_record", &block)`: the
    // statements SQLite finished on the session's connection while `work` ran. SQLite also reports
    // the ones FTS5 runs on its shadow tables (`'main'.'message_search_index_idx'`,
    // `PRAGMA 'main'.data_version`), which Active Record never sees, so those are left out.
    public static (T Result, List<string> Statements) Recording<T>(SqliteSession session, Func<T> work)
    {
        var connection = session.Connection.Handle!;
        var statements = new List<string>();
        raw.sqlite3_profile(connection, (_, sql, _) =>
        {
            if (!sql.Contains("'main'.", StringComparison.Ordinal))
            {
                statements.Add(sql);
            }
        }, null);
        try
        {
            return (work(), statements);
        }
        finally
        {
            raw.sqlite3_profile(connection, (strdelegate_profile)null!, null);
        }
    }
}
