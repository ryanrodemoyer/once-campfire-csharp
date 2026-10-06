using System.Text.Json.Nodes;
using Campfire.Data.Pagination;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests.Pagination;

// Every case in Oracle/pagination.json: what reference/app/models/message/pagination.rb returned
// on the parity seed, for every room and message in it, against what MessagePages returns on the
// same database.
public class PaginationOracleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record OracleCase(string Query, JsonArray Args, JsonNode? Result);

    static readonly Lazy<JsonNode> Oracle = new(() => JsonNode.Parse(File.ReadAllText(TestDatabase.Oracle("pagination.json")))!);

    static List<OracleCase> Cases =>
        [.. Oracle.Value["cases"]!.AsArray().Select(node => new OracleCase(node!["query"]!.GetValue<string>(), node["args"]!.AsArray(), node["result"]))];

    static Dictionary<string, long> Labels =>
        Oracle.Value["labels"]!.AsObject().ToDictionary(label => label.Key, label => label.Value!.GetValue<long>());

    public static TheoryData<string> QueryNames() => [.. Cases.Select(c => c.Query).Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_oracle_query_has_a_csharp_counterpart()
    {
        var missing = QueryNames().Select(row => row.Data).Except(Queries.Keys).ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(QueryNames))]
    public async Task Page_is_what_rails_returns(string query)
    {
        var mismatches = await Run(Cases.Where(c => c.Query == query));
        Assert.Empty(mismatches);
    }

    // The acceptance criterion: page boundaries on the seed for every busy_* label.
    [Fact]
    public async Task Busy_labels_page_like_rails()
    {
        var busy = Labels.Where(label => label.Key.StartsWith("busy_", StringComparison.Ordinal)).ToDictionary();
        Assert.Equal(120, busy.Count);

        var paging = new[] { "PageBefore", "PageAfter", "PageAround" };
        var cases = Cases.Where(c => paging.Contains(c.Query) && busy.ContainsValue(c.Args[1]!.GetValue<long>())).ToList();
        Assert.Equal(busy.Count * paging.Length, cases.Count);
        Assert.Empty(await Run(cases));

        // The seed's own notes (reference-rust/parity/screens.yml): the oldest has nothing before
        // it, and the middle loads the full window.
        Assert.Equal(1 + MessagePages.PageSize, Result(cases, "PageAround", busy["busy_001"]).Count);
        Assert.Equal((2 * MessagePages.PageSize) + 1, Result(cases, "PageAround", busy["busy_060"]).Count);
    }

    static JsonArray Result(List<OracleCase> cases, string query, long messageId) =>
        cases.Single(c => c.Query == query && c.Args[1]!.GetValue<long>() == messageId).Result!.AsArray();

    static async Task<List<string>> Run(IEnumerable<OracleCase> cases)
    {
        using var file = TestDatabase.FromParitySeed();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        return await database.ReadAsync(session =>
            cases.Select(c => (c, Expected: c.Result?.ToJsonString(), Actual: Queries[c.Query](session, c.Args)?.ToJsonString()))
                .Where(r => r.Expected != r.Actual)
                .Select(r => $"{r.c.Query}({r.c.Args.ToJsonString()}): rails {r.Expected}, c# {r.Actual}")
                .ToList(), Ct);
    }

    delegate JsonNode? Query(SqliteSession session, JsonArray args);

    static readonly Dictionary<string, Query> Queries = new()
    {
        ["LastPage"] = (s, a) => Ids(MessagePages.LastPage(s, Long(a[0]))),
        ["FirstPage"] = (s, a) => Ids(MessagePages.FirstPage(s, Long(a[0]))),
        ["IsPaged"] = (s, a) => MessagePages.IsPaged(s, Long(a[0])),
        ["PageBefore"] = (s, a) => Ids(MessagePages.PageBefore(s, Long(a[0]), Message(s, a))),
        ["PageAfter"] = (s, a) => Ids(MessagePages.PageAfter(s, Long(a[0]), Message(s, a))),
        ["PageAround"] = (s, a) => Ids(MessagePages.PageAround(s, Long(a[0]), Message(s, a))),
        ["ExistsBefore"] = (s, a) => MessagePages.ExistsBefore(s, Long(a[0]), Message(s, a)),
        ["ExistsAfter"] = (s, a) => MessagePages.ExistsAfter(s, Long(a[0]), Message(s, a)),
        ["PageCreatedSince"] = (s, a) => Ids(MessagePages.PageCreatedSince(s, Long(a[0]), Since(a[1]))),
        ["PageUpdatedSince"] = (s, a) => Ids(MessagePages.PageUpdatedSince(s, Long(a[0]), Since(a[1]), [.. a[2]!.AsArray().Select(Long)])),
    };

    static long Long(JsonNode? node) => node!.GetValue<long>();

    static Message Message(SqliteSession session, JsonArray args) => Messages.FindInRoom(session, Long(args[0]), Long(args[1]))!;

    // `Time.at(0, params[:since].to_i, :millisecond)`
    static DateTimeOffset Since(JsonNode? node) => DateTimeOffset.FromUnixTimeMilliseconds(Long(node));

    static JsonArray Ids(IEnumerable<Message> messages) => new([.. messages.Select(message => JsonValue.Create(message.Id))]);
}
