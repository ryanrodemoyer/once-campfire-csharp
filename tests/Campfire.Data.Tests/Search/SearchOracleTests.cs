using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Searching;
using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Tests.Searching;

// Every case in Oracle/search.json (Oracle/search.rb): what the reference's SearchesController
// query, Message::Searchable and Search did with a seeded random corpus of queries, against what
// SearchQuery, MessageSearch and RecentSearches do with the same input on the same database.
public class SearchOracleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly Lazy<JsonNode> Oracle = new(() => JsonNode.Parse(File.ReadAllText(TestDatabase.Oracle("search.json")))!);

    static JsonArray Cases(string name) => Oracle.Value[name]!.AsArray();

    [Fact]
    public void Queries_are_sanitized_like_rails()
    {
        var queries = Cases("queries");
        Assert.True(queries.Count > 600);

        var mismatches = queries
            .Select(c => (Q: c!["q"]?.GetValue<string>(), Query: c["query"]?.GetValue<string>(), Present: c["present"]!.GetValue<bool>()))
            .Where(c => SearchQuery.Sanitize(c.Q) != c.Query || SearchQuery.IsPresent(SearchQuery.Sanitize(c.Q)) != c.Present)
            .Select(c => $"{Json(c.Q)}: rails {Json(c.Query)} ({c.Present}), c# {Json(SearchQuery.Sanitize(c.Q))}")
            .ToList();
        Assert.Empty(mismatches);
    }

    // The acceptance criterion: for every user in the parity seed and every present query of the
    // corpus (FTS syntax errors and mixed scripts included), the same ids in the same order, or
    // the same SQLite error.
    [Fact]
    public async Task Searches_find_what_rails_finds_or_fail_as_it_fails()
    {
        var searches = Cases("searches");
        Assert.True(searches.Count > 5000);
        Assert.Contains(searches, c => c!["error"] is not null);

        using var file = TestDatabase.FromParitySeed();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        Assert.Empty(await Compare(database, searches));
    }

    // The seed has no `created_at` ties and no query reaching `last_page_of(100)`; the oracle's
    // crowded room has both, its messages indexed by the reference's `create_in_index`, here by
    // Lifecycle/MessageSearchIndex's.
    [Fact]
    public async Task A_crowded_room_pages_and_breaks_ties_like_rails()
    {
        var crowded = Oracle.Value["crowded"]!;
        var searches = crowded["searches"]!.AsArray();
        Assert.Contains(searches, c => c!["result"]!.AsArray().Count == MessageSearch.Limit);

        using var file = TestDatabase.FromParitySeed();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        var created = await database.WriteAsync(tx => crowded["messages"]!.AsArray().Select(m =>
        {
            var at = DateTimeOffset.Parse(m!["at"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var message = Messages.Create(tx.Session, m["room"]!.GetValue<long>(), m["creator"]!.GetValue<long>(), m["client_message_id"]!.GetValue<string>(), at);
            MessageSearchIndex.Create(tx.Session, message.Id, m["body"]!.GetValue<string>());
            return message.Id == m["id"]!.GetValue<long>();
        }).ToList(), Ct);
        Assert.All(created, Assert.True);
        Assert.Empty(await Compare(database, searches));
    }

    static Task<List<string>> Compare(SqliteDatabase database, JsonArray searches) =>
        database.ReadAsync(session => searches.Select(c =>
        {
            var user = c!["user"]!.GetValue<long>();
            var q = c["q"]!.GetValue<string>();
            var expected = c["error"] is { } error ? $"error {error.GetValue<string>()}" : c["result"]!.ToJsonString();
            var actual = Search(session, user, q);
            return expected == actual ? null : $"user {user}, q {Json(q)}: rails {expected}, c# {actual}";
        }).OfType<string>().ToList(), Ct);

    static string Search(SqliteSession session, long userId, string q)
    {
        try
        {
            var messages = MessageSearch.Reachable(session, userId, SearchQuery.Searchable(q)!);
            return new JsonArray([.. messages.Select(message => JsonValue.Create(message.Id))]).ToJsonString();
        }
        catch (SqliteException exception)
        {
            return $"error {Sqlite3ErrorMessage(exception)}";
        }
    }

    // SQLite's own message, which the sqlite3 gem raises as is: Microsoft.Data.Sqlite wraps it as
    // "SQLite Error 1: '<message>'.".
    static string Sqlite3ErrorMessage(SqliteException exception)
    {
        var message = exception.Message;
        var start = message.IndexOf('\'', StringComparison.Ordinal);
        return start < 0 ? message : message[(start + 1)..message.LastIndexOf('\'')];
    }

    [Fact]
    public async Task Recent_searches_are_recorded_trimmed_and_cleared_like_rails()
    {
        var steps = Cases("recent");
        Assert.True(steps.Count > 20);

        using var fixtures = new Fixtures.Database();
        var mismatches = new List<string>();
        foreach (var step in steps)
        {
            var user = step!["user"]!.GetValue<long>();
            var q = step["q"]?.GetValue<string>();
            var at = DateTimeOffset.Parse(step["at"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            string actual;
            try
            {
                await fixtures.Db.WriteAsync(tx =>
                {
                    if (step["clear"]!.GetValue<bool>()) RecentSearches.Clear(tx, user);
                    else RecentSearches.Record(tx, user, q!, at);
                }, Ct);
                actual = (await fixtures.Db.ReadAsync(session => Recent(RecentSearches.Ordered(session, user)), Ct)).ToJsonString();
            }
            catch (ArgumentNullException)
            {
                // Rails' ActiveRecord::NotNullViolation for `searches.query`.
                actual = "error ActiveRecord::NotNullViolation";
            }
            var expected = step["error"] is { } error ? $"error {error.GetValue<string>()}" : step["searches"]!.ToJsonString();
            if (expected != actual) mismatches.Add($"step {step["step"]}: rails {expected}, c# {actual}");
        }
        Assert.Empty(mismatches);
    }

    static JsonArray Recent(List<Search> searches) =>
        new([.. searches.Select(search => new JsonArray(search.Query, Iso8601(search.CreatedAt), Iso8601(search.UpdatedAt)))]);

    static string Iso8601(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    static string Json(string? text) => text is null ? "null" : JsonValue.Create(text).ToJsonString();
}
