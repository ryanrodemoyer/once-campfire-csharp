using Campfire.Data.Sqlite;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary>A signed-in app on the parity seed, the way the reference cache tests sign David in.</summary>
static class CacheHarness
{
    public const long AllTalk = 486777696;
    public const long Pets = 104393281;
    public const long Designers = 654632876;
    public const long David = 127326141;
    public const long Jason = 149087659;
    public const string DavidSession = "AxJs94fteQ5Autv2VrKsH68c";
    public const string BenderKey = "394959859-BenderBot123";

    public static MessagesApp Open() => new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    public static Dictionary<string, string> DavidHeaders(MessagesApp app, string accept = "text/html, application/xhtml+xml") =>
        app.SignedIn(DavidSession, accept);

    public static Task<Response> GetAsync(MessagesApp app, string path, Dictionary<string, string>? headers = null) =>
        app.SendAsync("GET", path, headers ?? DavidHeaders(app));

    /// <summary>
    /// David's session is an hour stale, so the first authenticated GET writes and the captured
    /// version cannot be admitted. The second GET caches. Clearing then lets a test count writes.
    /// </summary>
    public static async Task WarmAsync(MessagesApp app)
    {
        await GetAsync(app, $"/rooms/{AllTalk}");
        await GetAsync(app, $"/rooms/{AllTalk}");
        app.App.Responses.Clear();
    }

    public static string? Header(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var value) ? value.ToString() : null;

    public static void Execute(MessagesApp app, string sql)
    {
        using var connection = app.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(MessagesApp app, string sql) => (long)app.Scalar(sql)!;

    public static async Task<List<string>> QueriesAsync(MessagesApp app, string path, Dictionary<string, string>? headers = null)
    {
        var sql = new List<string>();
        SqliteSession.QueryListener.Value = sql.Add;
        try
        {
            await GetAsync(app, path, headers);
            return sql;
        }
        finally
        {
            SqliteSession.QueryListener.Value = null;
        }
    }
}
