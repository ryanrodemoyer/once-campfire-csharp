using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Data.Tests.Queries;

// reference/test/models/membership_test.rb. `travel_to CONNECTION_TTL.from_now + 1` is a later
// `now`. "removing a membership resets the user's connections" is an after_destroy_commit
// callback, D03's.
public sealed class MembershipTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt;
    static readonly DateTimeOffset Later = Now + Membership.ConnectionTtl + TimeSpan.FromSeconds(1);

    readonly Fixtures.Database fixtures = new();
    readonly long id = Fixtures.Id("david_watercooler");

    public void Dispose() => fixtures.Dispose();

    Membership Load() => fixtures.Write(session => Memberships.Find(session, id)!);

    Membership Connected(Membership membership, DateTimeOffset now) => fixtures.Write(session => Memberships.Connected(session, membership, now));

    Membership Disconnected(Membership membership, DateTimeOffset now) => fixtures.Write(session => Memberships.Disconnected(session, membership, now));

    bool InConnected(DateTimeOffset now) => fixtures.Write(session => Memberships.Connected(session, now).Any(m => m.Id == id));

    bool InDisconnected(DateTimeOffset now) => fixtures.Write(session => Memberships.Disconnected(session, now).Any(m => m.Id == id));

    [Fact]
    public void Connected_scope()
    {
        var membership = Connected(Load(), Now);
        Assert.True(InConnected(Now));

        Disconnected(membership, Now);
        Assert.False(InConnected(Now));

        Connected(Load(), Now);
        Assert.False(InConnected(Later));
    }

    [Fact]
    public void Disconnected_scope()
    {
        var membership = Disconnected(Load(), Now);
        Assert.True(InDisconnected(Now));

        Connected(membership, Now);
        Assert.False(InDisconnected(Now));

        Assert.True(InDisconnected(Later));
    }

    [Fact]
    public void Connected_is_false_when_connection_is_stale()
    {
        var membership = Connected(Load(), Now);
        Assert.False(membership.IsConnected(Later));
    }

    [Fact]
    public void Connecting()
    {
        var membership = Connected(Load(), Now);
        Assert.True(membership.IsConnected(Now));
        Assert.Equal(1, membership.Connections);

        membership = Connected(membership, Now);
        Assert.Equal(2, membership.Connections);
    }

    [Fact]
    public void Connecting_resets_stale_connection_count()
    {
        var membership = Connected(Connected(Load(), Now), Now);
        Assert.Equal(2, membership.Connections);

        membership = Connected(membership, Later);
        Assert.Equal(1, membership.Connections);
    }

    [Fact]
    public void Disconnecting()
    {
        var membership = Connected(Connected(Load(), Now), Now);

        membership = Disconnected(membership, Now);
        Assert.True(membership.IsConnected(Now));
        Assert.Equal(1, membership.Connections);

        membership = Disconnected(membership, Now);
        Assert.False(membership.IsConnected(Now));
        Assert.Equal(0, membership.Connections);
    }

    [Fact]
    public void Disconnecting_resets_stale_connection_count()
    {
        var membership = Connected(Connected(Load(), Now), Now);
        Assert.Equal(2, membership.Connections);

        membership = Disconnected(membership, Later);
        Assert.Equal(0, membership.Connections);
    }

    [Fact]
    public void Refreshing_the_connection()
    {
        var membership = Connected(Load(), Now);
        Assert.False(membership.IsConnected(Later));

        membership = fixtures.Write(session => Memberships.RefreshConnection(session, membership, Later));
        Assert.True(membership.IsConnected(Later));
    }

    // Membership::Connectable's other methods, which the presence channel calls.

    [Fact]
    public void Present_counts_a_connection_and_reads_the_room()
    {
        fixtures.Write(session => session.Execute("UPDATE memberships SET unread_at = @t WHERE id = @id", ("@t", "2026-03-02 15:00:00"), ("@id", id)));
        var before = Load();

        var membership = fixtures.Write(session => Memberships.Present(session, before, Now));
        Assert.Equal(1, membership.Connections);
        Assert.Equal(Now, membership.ConnectedAt);
        Assert.Null(membership.UnreadAt);
        Assert.Equal(before.UpdatedAt, membership.UpdatedAt);

        membership = fixtures.Write(session => Memberships.Present(session, membership, Now));
        Assert.Equal(2, membership.Connections);
        Assert.Equal(1, fixtures.Write(session => Memberships.Present(session, membership, Later)).Connections);
    }

    [Fact]
    public void Disconnect_all_disconnects_only_the_connected()
    {
        Connected(Connected(Load(), Now), Now);

        Assert.Equal(1, fixtures.Write(session => Memberships.DisconnectAll(session, Now)));
        var membership = Load();
        Assert.Null(membership.ConnectedAt);
        Assert.Equal(0, membership.Connections);
        Assert.Equal(0, fixtures.Write(session => Memberships.DisconnectAll(session, Now)));
    }

    [Fact]
    public void Connection_writes_are_the_ones_rails_makes()
    {
        var at = Now + TimeSpan.FromMinutes(5);

        // Not connected: `update!(connections: 1)` and `touch :connected_at`, both stamping updated_at.
        var membership = Connected(Load(), at);
        Assert.Equal((1L, at, at), (membership.Connections, membership.ConnectedAt!.Value, membership.UpdatedAt));

        // Connected: `increment!(:connections, touch: true)`.
        var later = at + TimeSpan.FromSeconds(10);
        membership = Connected(membership, later);
        Assert.Equal((2L, later, later), (membership.Connections, membership.ConnectedAt!.Value, membership.UpdatedAt));

        // Reading clears unread_at, and saves nothing when it's already clear.
        var read = fixtures.Write(session => Memberships.Read(session, membership, later + TimeSpan.FromMinutes(1)));
        Assert.Equal(membership, read);
    }

    [Fact]
    public void Visible_disconnected_members_other_than_the_creator_are_marked_unread()
    {
        var watercooler = Fixtures.Id("watercooler");
        var david = Fixtures.Id("david");
        Connected(fixtures.Write(session => Memberships.FindFor(session, Fixtures.Id("jason"), watercooler)!), Now);

        var marked = fixtures.Write(session => Memberships.MarkUnread(session, watercooler, david, Now - TimeSpan.FromMinutes(1), Now));

        // Bender is the only one left: David wrote it and Jason is connected.
        Assert.Equal(1, marked);
        var unread = fixtures.Write(session => Memberships.ForRoom(session, watercooler).Where(m => m.IsUnread).Select(m => m.UserId).ToList());
        Assert.Equal([Fixtures.Id("bender")], unread);
    }
}
