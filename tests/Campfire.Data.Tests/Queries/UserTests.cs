using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Data.Tests.Queries;

// reference/test/models/user_test.rb, user/bot_test.rb and user/role_test.rb. The callbacks
// these chain together (after_create_commit granting open rooms, `deactivate`'s transaction and
// disconnects) are D03's; here they're the queries those callbacks run.
public sealed class UserTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt;

    readonly Fixtures.Database fixtures = new();

    public void Dispose() => fixtures.Dispose();

    static long Id(string label) => Fixtures.Id(label);

    static User CreateNewUser(Campfire.Data.Sqlite.SqliteSession session) =>
        Users.Create(session, "User", "user@example.com", "digest", Now);

    [Fact]
    public void Creating_users_grants_membership_to_the_open_rooms()
    {
        var (before, opens, after) = fixtures.Write(session =>
        {
            var before = Memberships.Count(session);
            var user = CreateNewUser(session);
            Memberships.GrantRoomsTo(session, user.Id, Rooms.IdsOfType(session, RoomType.Open));
            return (before, Rooms.CountOfType(session, RoomType.Open), Memberships.Count(session));
        });
        Assert.Equal(before + opens, after);
    }

    [Fact]
    public void Creating_subsequent_users_makes_them_active_members()
    {
        var user = fixtures.Write(session => CreateNewUser(session));
        Assert.True(user.IsMember);
        Assert.True(user.IsActive);
        Assert.Equal((Now, Now), (user.CreatedAt, user.UpdatedAt));
    }

    [Fact]
    public void Deactivating_a_user_deletes_push_subscriptions_searches_memberships_for_non_direct_rooms_and_sessions_and_changes_their_email_address()
    {
        var david = Id("david");
        var (expected, before) = fixtures.Write(session => (
            (Memberships.CountForUserWithoutDirectRooms(session, david), PushSubscriptions.CountForUser(session, david), Searches.CountForUser(session, david), Sessions.CountForUser(session, david)),
            (Memberships.Count(session), PushSubscriptions.Count(session), Searches.Count(session))));
        Assert.Equal(1, expected.Item4);

        var deactivated = fixtures.Write(session =>
        {
            Memberships.DeleteForUserWithoutDirectRooms(session, david);
            PushSubscriptions.DeleteForUser(session, david);
            Searches.DeleteForUser(session, david);
            Sessions.DeleteForUser(session, david);
            var user = Users.Find(session, david)!;
            return Users.Update(session, user, Now, status: UserStatus.Deactivated,
                emailAddress: new(User.DeactivatedEmailAddress(user.EmailAddress, "2e7de450-cf04-4fa8-9b02-ff5ab2d733e7")));
        });

        var after = fixtures.Write(session => (Memberships.Count(session), PushSubscriptions.Count(session), Searches.Count(session)));
        Assert.Equal((before.Item1 - expected.Item1, before.Item2 - expected.Item2, before.Item3 - expected.Item3), after);
        Assert.Equal(0, fixtures.Write(session => Sessions.CountForUser(session, david)));
        Assert.Equal("david-deactivated-2e7de450-cf04-4fa8-9b02-ff5ab2d733e7@37signals.com", deactivated.EmailAddress);
        Assert.True(deactivated.IsDeactivated);
        Assert.NotEmpty(fixtures.Write(session => Rooms.ForUserOfType(session, david, RoomType.Direct)));
    }

    [Fact]
    public void Create_bot()
    {
        var bot = fixtures.Write(session => Users.CreateBot(session, "Bender", Now));
        Assert.Matches("^[A-Za-z0-9]{12}$", bot.BotToken);
        Assert.Equal($"{bot.Id}-{bot.BotToken}", bot.BotKey);
        Assert.True(bot.IsBot);
        Assert.Null(bot.EmailAddress);
    }

    [Fact]
    public void Reset_bot_key()
    {
        var bot = fixtures.Write(session => Users.CreateBot(session, "Bender", Now));
        var reset = fixtures.Write(session => Users.ResetBotKey(session, bot, Now + TimeSpan.FromMinutes(1)));
        Assert.NotEqual(bot.BotToken, reset.BotToken);
        Assert.Equal($"{bot.Id}-{reset.BotToken}", reset.BotKey);
        Assert.Equal(Now + TimeSpan.FromMinutes(1), reset.UpdatedAt);
    }

    [Fact]
    public void Authenticate()
    {
        var bot = fixtures.Write(session => Users.CreateBot(session, "Bender", Now));
        Assert.Equal(bot, fixtures.Write(session => Users.AuthenticateBot(session, bot.BotKey)));
        Assert.Null(fixtures.Write(session => Users.AuthenticateBot(session, $"{bot.Id}-wrong")));

        var deactivated = fixtures.Write(session => Users.Update(session, bot, Now, status: UserStatus.Deactivated));
        Assert.Null(fixtures.Write(session => Users.AuthenticateBot(session, deactivated.BotKey)));
    }

    [Fact]
    public void Can_administer()
    {
        Assert.True(NewUser(UserRole.Administrator).CanAdminister());
        Assert.False(NewUser(UserRole.Member).CanAdminister());
    }

    [Fact]
    public void Can_administer_a_record()
    {
        var member = NewUser(UserRole.Member, id: 1);
        Assert.True(member.CanAdminister(creatorId: member.Id));

        var anotherMember = NewUser(UserRole.Member, id: 2);
        var designers = fixtures.Write(session => Rooms.Find(session, Id("designers"))!);
        Assert.False(anotherMember.CanAdminister(designers.CreatorId));
        Assert.True(NewUser(UserRole.Administrator, id: 3).CanAdminister(designers.CreatorId));
    }

    [Fact]
    public void Mentionees_are_the_mentioned_users_in_the_room()
    {
        // message_test.rb "mentionees": David is in pets, Kevin isn't.
        var pets = Id("pets");
        Assert.Equal([Id("david")], fixtures.Write(session => Users.InRoomWhereIds(session, pets, [Id("david")])).Select(u => u.Id));
        Assert.Empty(fixtures.Write(session => Users.InRoomWhereIds(session, pets, [Id("kevin")])));
    }

    [Theory]
    [InlineData("David", "D")]
    [InlineData("Jason Fried", "JF")]
    [InlineData("jean-luc O'Neil", "jlON")]
    [InlineData("a-b c_d 3x", "abc3")]
    [InlineData("élan Über ñ", "")]
    [InlineData("xéy z", "xz")]
    [InlineData("日本 語", "")]
    public void Initials_are_what_ruby_scans(string name, string initials)
    {
        // Expected values from Ruby 3.4: `name.scan(/\b\w/).join`.
        Assert.Equal(initials, (NewUser(UserRole.Member) with { Name = name }).Initials);
    }

    [Fact]
    public void Title_joins_the_name_and_a_present_bio()
    {
        Assert.Equal("JZ – Designer", (NewUser(UserRole.Member) with { Name = "JZ", Bio = "Designer" }).Title);
        Assert.Equal("JZ", (NewUser(UserRole.Member) with { Name = "JZ", Bio = " " }).Title);
    }

    static User NewUser(UserRole role, long id = 0) =>
        new(id, "User", null, null, role, UserStatus.Active, null, null, Now, Now);
}
