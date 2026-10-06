using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Data.Tests.Queries;

// reference/app/models/session.rb, search.rb and ban.rb.
public sealed class SessionTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt;

    readonly Fixtures.Database fixtures = new();

    public void Dispose() => fixtures.Dispose();

    [Fact]
    public void Starting_a_session_gives_it_a_token_and_activity()
    {
        var session = fixtures.Write(s => Sessions.Start(s, Fixtures.Id("kevin"), "Firefox", "203.0.113.1", Now));
        Assert.Matches("^[1-9A-HJ-NP-Za-km-z]{24}$", session.Token);
        Assert.Equal((Now, Now, Now), (session.LastActiveAt, session.CreatedAt, session.UpdatedAt));
        Assert.Equal(session, fixtures.Write(s => Sessions.FindByToken(s, session.Token)));
    }

    [Fact]
    public void Resuming_refreshes_activity_once_an_hour()
    {
        // The fixture was last active 2 hours before the fixtures were loaded.
        var session = fixtures.Write(s => Sessions.FindByToken(s, "AxJs94fteQ5Autv2VrKsH68c")!);
        Assert.Equal(Now - TimeSpan.FromHours(2), session.LastActiveAt);

        var resumed = fixtures.Write(s => Sessions.Resume(s, session, "Firefox", "203.0.113.1", Now));
        Assert.Equal((Now, Now, "Firefox", "203.0.113.1"), (resumed.LastActiveAt, resumed.UpdatedAt, resumed.UserAgent, resumed.IpAddress));

        var later = Now + TimeSpan.FromMinutes(59);
        Assert.Same(resumed, fixtures.Write(s => Sessions.Resume(s, resumed, "Chrome", "203.0.113.2", later)));
        Assert.Equal(resumed, fixtures.Write(s => Sessions.Find(s, resumed.Id)));

        // `before?` is strict: exactly an hour later is not yet stale.
        Assert.False(resumed.NeedsActivityRefresh(Now + Session.ActivityRefreshRate));
        Assert.True(resumed.NeedsActivityRefresh(Now + Session.ActivityRefreshRate + TimeSpan.FromTicks(10)));
    }

    [Fact]
    public void Trimming_keeps_the_ten_most_recent_searches()
    {
        var david = Fixtures.Id("david");
        var kept = fixtures.Write(s =>
        {
            for (var i = 1; i <= 12; i++)
            {
                Searches.Create(s, david, $"query {i}", Now + TimeSpan.FromMinutes(i));
            }
            Searches.TrimRecent(s, david);
            return Searches.ForUserOrdered(s, david).Select(search => search.Query).ToList();
        });
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(i => $"query {i}"), kept);
    }

    [Fact]
    public void Banned_ip_addresses()
    {
        fixtures.Write(s => Bans.Create(s, Fixtures.Id("kevin"), "203.0.113.9", Now));
        Assert.True(fixtures.Write(s => Bans.IsBanned(s, "203.0.113.9")));
        Assert.False(fixtures.Write(s => Bans.IsBanned(s, "203.0.113.10")));
        Assert.Equal(1, fixtures.Write(s => Bans.DeleteForUser(s, Fixtures.Id("kevin"))));
        Assert.False(fixtures.Write(s => Bans.IsBanned(s, "203.0.113.9")));
    }

    [Fact]
    public void Bot_webhooks()
    {
        var bender = Fixtures.Id("bender");
        var webhook = fixtures.Write(s => Webhooks.ForUser(s, bender)!);
        Assert.Equal("http://example.com/bender", webhook.Url);

        var updated = fixtures.Write(s => Webhooks.UpdateUrl(s, webhook, "https://example.com/new", Now + TimeSpan.FromMinutes(1)));
        Assert.Equal(("https://example.com/new", Now + TimeSpan.FromMinutes(1)), (updated.Url, updated.UpdatedAt));
        Assert.Equal(1, fixtures.Write(s => Webhooks.DeleteForUser(s, bender)));
        Assert.Null(fixtures.Write(s => Webhooks.ForUser(s, bender)));
    }

    [Fact]
    public void Messages_boosts_and_rich_texts()
    {
        var room = Fixtures.Id("designers");
        var jason = Fixtures.Id("jason");
        var (message, boost, body) = fixtures.Write(s =>
        {
            var message = Messages.Create(s, room, jason, null, Now);
            var boost = Boosts.Create(s, message.Id, Fixtures.Id("david"), "👍", Now);
            var body = RichTexts.Create(s, RecordTypes.Message, message.Id, AttachmentNames.Body, "<p>Hi</p>", Now);
            return (message, boost, body);
        });
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", message.ClientMessageId);
        Assert.Equal(message, fixtures.Write(s => Messages.FindInRoom(s, room, message.Id)));
        Assert.Equal([boost], fixtures.Write(s => Boosts.ForMessageOrdered(s, message.Id)));
        Assert.Equal(body, fixtures.Write(s => RichTexts.For(s, "Message", message.Id, "body")));
        Assert.Equal(message.Id, fixtures.Write(s => Messages.InRoomOrdered(s, room)).Last().Id);
    }
}
