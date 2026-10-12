using Campfire.Data.Searching;
using static Campfire.Web.Tests.Upstream.U05.Seed;

namespace Campfire.Web.Tests.Upstream.U05;

/// <summary>
/// Search operator words as words (f2bdfde), the newest matches read off the index (77c5234) and
/// bounded accessible probes (dbc7620): reference/test/controllers/searches_controller_test.rb,
/// models/message_test.rb and models/message/searchable_test.rb's cases, on the default parity seed.
/// </summary>
public sealed class SearchTests : IDisposable
{
    readonly Controllers.Messages.MessagesApp app = App();

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Operator_words_are_searched_for_as_words()
    {
        await PostAsync(app, Designers, "Salt and pepper", "operators");

        var response = await app.SendAsync("GET", "/searches?q=salt+AND", app.SignedIn(DavidSession, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.Contains("Salt and pepper", response.Body, StringComparison.Ordinal);
    }

    // The seed has messages with "and", "or" and "not" in them, so only the answer is asserted.
    [Theory]
    [InlineData("AND"), InlineData("OR"), InlineData("NOT")]
    public async Task Searching_for_an_operator_word_alone_doesnt_fail(string word)
    {
        var response = await app.SendAsync("GET", $"/searches?q={word}", app.SignedIn(DavidSession, "text/html"));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task Search_reads_quotes_and_operator_words_in_the_query_as_text()
    {
        var message = await PostAsync(app, Designers, "Say hi, NOT bye", "quoted");

        var found = await app.Database.ReadAsync(session => MessageSearch.Reachable(session, David, "say \"hi NOT"), Cancellation);

        Assert.Equal([message], found.Select(m => m.Id));
    }

    [Theory]
    [InlineData("AND", "\"AND\"")]
    [InlineData("salt AND", "\"salt\" \"AND\"")]
    [InlineData("say \"hi NOT", "\"say\" \"\"\"hi\" \"NOT\"")]
    public void Match_terms_quote_each_word(string query, string terms) => Assert.Equal(terms, MessageSearch.MatchTerms(query));

    [Fact]
    public async Task The_last_page_of_matches_holds_the_newest_ones_read_off_the_index_without_sorting_every_match()
    {
        var messages = new List<long>();
        foreach (var body in new[] { "first cat", "second cat", "third cat" })
        {
            messages.Add(await PostAsync(app, Designers, body, body));
        }

        var (page, plan) = await app.Database.ReadAsync(session =>
        {
            var (page, statements) = Recording(session, () => MessageSearch.LastPageOfMatches(session, David, "cat", 2));
            var search = Assert.Single(statements, sql => sql.Contains("message_search_index", StringComparison.Ordinal));
            var plan = session.Query($"EXPLAIN QUERY PLAN {search}", reader => reader.GetString(3), ("@user_id", David), ("@query", MessageSearch.MatchTerms("cat")), ("@size", 2));
            return (page, string.Join(" | ", plan));
        }, Cancellation);

        Assert.Equal(messages[^2..], page.Select(m => m.Id));
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bounded_search_keeps_older_reachable_hits_behind_inaccessible_matches()
    {
        var accessible = await PostAsync(app, Designers, "bounded sparse needle", "accessible");
        Execute(app, $"""
            INSERT INTO rooms (name, type, creator_id, created_at, updated_at) VALUES ('Private search history', 'Rooms::Closed', {Jason}, '2026-03-02 16:00:00', '2026-03-02 16:00:00');
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 1099)
            INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at)
            SELECT (SELECT MAX(id) FROM rooms), {Jason}, 'private-' || i, '2026-03-02 16:00:00', '2026-03-02 16:00:00' FROM n;
            INSERT INTO message_search_index (rowid, body) SELECT id, 'bounded sparse needle' FROM messages WHERE client_message_id LIKE 'private-%';
            """);

        Assert.Equal([accessible], await SearchAsync("bounded sparse"));

        Execute(app, $"DELETE FROM memberships WHERE room_id = {Designers} AND user_id = {David}");
        Assert.Empty(await SearchAsync("bounded sparse"));
    }

    async Task<List<long>> SearchAsync(string query) =>
        await app.Database.ReadAsync(session => MessageSearch.Reachable(session, David, query).Select(m => m.Id).ToList(), Cancellation);
}
