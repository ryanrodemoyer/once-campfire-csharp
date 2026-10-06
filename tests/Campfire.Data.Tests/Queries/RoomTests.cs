using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Data.Tests.Queries;

// reference/test/models/room_test.rb and rooms/direct_test.rb. Rooms::Open granting itself to
// everyone (rooms/open_test.rb) is an after_save_commit callback, D03's.
public sealed class RoomTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt;

    readonly Fixtures.Database fixtures = new();

    public void Dispose() => fixtures.Dispose();

    static long User(string label) => Fixtures.Id(label);

    Room Room(string label) => fixtures.Write(session => Rooms.Find(session, Fixtures.Id(label))!);

    List<long> UserIds(Room room) => fixtures.Write(session => Users.IdsInRoom(session, room.Id));

    [Fact]
    public void Grant_membership_to_user()
    {
        var watercooler = Room("watercooler");
        fixtures.Write(session => { Memberships.GrantTo(session, watercooler, [User("kevin")]); return 0; });
        Assert.Contains(User("kevin"), UserIds(watercooler));
    }

    [Fact]
    public void Granting_skips_existing_members_and_uses_the_default_involvement()
    {
        var watercooler = Room("watercooler");
        var before = fixtures.Write(session => Memberships.Count(session));

        fixtures.Write(session => { Memberships.GrantTo(session, watercooler, [User("kevin"), User("david"), User("kevin")]); return 0; });

        Assert.Equal(before + 1, fixtures.Write(session => Memberships.Count(session)));
        Assert.Equal(Involvement.Everything, fixtures.Write(session => Memberships.FindFor(session, User("david"), watercooler.Id))!.Involvement);
        Assert.Equal(Involvement.Mentions, fixtures.Write(session => Memberships.FindFor(session, User("kevin"), watercooler.Id))!.Involvement);
    }

    [Fact]
    public void Revoke_membership_from_user()
    {
        var watercooler = Room("watercooler");
        var revoked = fixtures.Write(session => Memberships.RevokeFrom(session, watercooler.Id, [User("david")]));
        Assert.Equal([Fixtures.Id("david_watercooler")], revoked.Select(m => m.Id));
        Assert.DoesNotContain(User("david"), UserIds(watercooler));
    }

    [Fact]
    public void Revise_memberships()
    {
        var watercooler = Room("watercooler");
        fixtures.Write(session =>
        {
            Memberships.GrantTo(session, watercooler, [User("kevin")]);
            return Memberships.RevokeFrom(session, watercooler.Id, [User("david")]);
        });
        Assert.Contains(User("kevin"), UserIds(watercooler));
        Assert.DoesNotContain(User("david"), UserIds(watercooler));
    }

    [Fact]
    public void Create_for_users_by_giving_them_immediate_membership()
    {
        var room = fixtures.Write(session => Rooms.CreateFor(session, RoomType.Closed, "Hello!", User("david"), [User("kevin"), User("david")], Now));
        Assert.Equal([User("david"), User("kevin")], UserIds(room).Order());
        Assert.Equal(("Hello!", RoomType.Closed, User("david")), (room.Name, room.Type, room.CreatorId));
        Assert.Equal((Now, Now), (room.CreatedAt, room.UpdatedAt));
    }

    [Fact]
    public void Type()
    {
        Assert.True(new Room(0, null, RoomType.Open, 0, Now, Now).IsOpen);
        Assert.False(new Room(0, null, RoomType.Open, 0, Now, Now).IsDirect);
        Assert.True(new Room(0, null, RoomType.Direct, 0, Now, Now).IsDirect);
        Assert.True(new Room(0, null, RoomType.Closed, 0, Now, Now).IsClosed);
    }

    [Fact]
    public void Default_involvement_for_new_users()
    {
        var room = fixtures.Write(session => Rooms.CreateFor(session, RoomType.Closed, "Hello!", User("david"), [User("kevin"), User("david")], Now));
        Assert.All(fixtures.Write(session => Memberships.ForRoom(session, room.Id)), m => Assert.Equal(Involvement.Mentions, m.Involvement));
    }

    [Fact]
    public void Direct_rooms_keep_their_type()
    {
        var direct = Room("david_and_jason");
        Assert.Throws<InvalidOperationException>(() => fixtures.Write(session => Rooms.Update(session, direct, Now, type: RoomType.Open)));

        var watercooler = Room("watercooler");
        var opened = fixtures.Write(session => Rooms.Update(session, watercooler, Now + TimeSpan.FromMinutes(1), type: RoomType.Open));
        Assert.Equal(RoomType.Open, opened.Type);
        Assert.Equal(RoomType.Closed, fixtures.Write(session => Rooms.Update(session, opened, Now, type: RoomType.Closed)).Type);
    }

    [Fact]
    public void Updating_nothing_writes_nothing()
    {
        var watercooler = Room("watercooler");
        Assert.Same(watercooler, fixtures.Write(session => Rooms.Update(session, watercooler, Now + TimeSpan.FromDays(1), name: new(watercooler.Name))));
        Assert.Equal(watercooler, Room("watercooler"));
    }

    // rooms/direct_test.rb: `Rooms::Direct.find_or_create_for(users)`
    Room FindOrCreateDirectFor(params long[] userIds) => fixtures.Write(session =>
        Rooms.FindDirectFor(session, userIds) ?? Rooms.CreateFor(session, RoomType.Direct, null, userIds[0], userIds, Now));

    [Fact]
    public void Create_direct_room_for_same_users()
    {
        var room = FindOrCreateDirectFor(User("david"), User("jz"));
        Assert.Contains(User("david"), UserIds(room));
        Assert.Contains(User("jz"), UserIds(room));
        Assert.DoesNotContain(User("jason"), UserIds(room));
    }

    [Fact]
    public void Only_one_direct_room_will_exist_for_the_same_users()
    {
        var room1 = FindOrCreateDirectFor(User("david"), User("jz"));
        var room2 = FindOrCreateDirectFor(User("jz"), User("david"));
        Assert.Equal(room1, room2);
        Assert.Equal(Fixtures.Id("david_and_kevin"), FindOrCreateDirectFor(User("kevin"), User("david")).Id);
    }

    [Fact]
    public void Default_involvement_for_new_users_of_a_direct_room()
    {
        var room = FindOrCreateDirectFor(User("david"), User("jz"));
        Assert.All(fixtures.Write(session => Memberships.ForRoom(session, room.Id)), m => Assert.Equal(Involvement.Everything, m.Involvement));
    }
}
