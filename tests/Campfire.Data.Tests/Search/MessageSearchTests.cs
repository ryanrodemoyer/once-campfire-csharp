using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Searching;

namespace Campfire.Data.Tests.Searching;

// reference/test/controllers/searches_controller_test.rb, at the data layer, on the fixtures.
public sealed class MessageSearchTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt + TimeSpan.FromDays(1);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly Fixtures.Database fixtures = new();
    readonly long david = Fixtures.Id("david");
    readonly long designers = Fixtures.Id("designers");

    public MessageSearchTests() =>
        // `rooms(:designers).messages.create! body: "Hello world!", client_message_id: "search", creator: users(:david)`,
        // indexed after the commit.
        fixtures.Db.WriteAsync(tx => MessageLifecycle.Create(tx, new DomainSeams(Discard.Instance, Discard.Instance, Discard.Instance),
            designers, david, "search", "<div>Hello world!</div>", "Hello world!", Now), Ct).GetAwaiter().GetResult();

    public void Dispose() => fixtures.Dispose();

    List<string> Found(string q) => fixtures.Write(s =>
        MessageSearch.Reachable(s, david, SearchQuery.Searchable(q)!).Select(message => message.ClientMessageId).ToList());

    [Fact]
    public void Finding_reachable_messages() => Assert.Contains("search", Found("hello"));

    [Fact]
    public void Unreachable_messages_are_not_found()
    {
        fixtures.Write(s => s.Execute("DELETE FROM memberships WHERE id = @id", ("@id", Fixtures.Id("david_designers"))));
        Assert.DoesNotContain("search", Found("hello"));
    }

    [Fact]
    public void Edits_and_deletes_are_reindexed()
    {
        var message = fixtures.Write(s => Messages.FindInRoom(s, designers, Messages.InRoom(s, designers).Single(m => m.ClientMessageId == "search").Id)!);
        fixtures.Write(s => { MessageSearchIndex.Update(s, message.Id, "Goodbye world!"); return 0; });
        Assert.DoesNotContain("search", Found("hello"));
        Assert.Contains("search", Found("goodbye"));

        fixtures.Write(s => { MessageSearchIndex.Remove(s, message.Id); return 0; });
        Assert.DoesNotContain("search", Found("goodbye"));
    }

    [Fact]
    public async Task Create_saves_the_search_term()
    {
        var before = fixtures.Write(s => Searches.CountForUser(s, david));
        await fixtures.Db.WriteAsync(tx => RecentSearches.Record(tx, david, SearchQuery.Sanitize("hello")!, Now), Ct);
        Assert.Equal(before + 1, fixtures.Write(s => Searches.CountForUser(s, david)));
        Assert.NotNull(fixtures.Write(s => Searches.FindForUser(s, david, "hello")));
    }

    [Fact]
    public async Task Clear_search_history()
    {
        Assert.NotEmpty(fixtures.Write(s => RecentSearches.Ordered(s, david)));
        await fixtures.Db.WriteAsync(tx => RecentSearches.Clear(tx, david), Ct);
        Assert.Empty(fixtures.Write(s => RecentSearches.Ordered(s, david)));
    }

    sealed class Discard : IBroadcaster, IJobQueue, IConnectionRevoker
    {
        public static readonly Discard Instance = new();
        public void Broadcast(string stream, string payload) { }
        public void Enqueue(Job job) { }
        public void Disconnect(long userId, bool reconnect) { }
    }
}
