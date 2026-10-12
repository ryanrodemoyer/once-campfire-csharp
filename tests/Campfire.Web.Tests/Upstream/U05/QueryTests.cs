using Campfire.Data.Pagination;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using static Campfire.Web.Tests.Upstream.U05.Seed;

namespace Campfire.Web.Tests.Upstream.U05;

/// <summary>
/// <c>paged?</c> without counting (d1f09b4), the refresh's replaced messages through the
/// updated-at index (1e0d353), a direct room with one query however many there are (87eafc0) and
/// <c>User.visible_to</c> (0f2a1da): reference/test/models/message_test.rb, message/pagination_test.rb,
/// rooms/direct_test.rb and user/bannable_test.rb's cases, on the default parity seed.
/// </summary>
public sealed class QueryTests : IDisposable
{
    readonly Controllers.Messages.MessagesApp app = App();

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Paged_tells_whether_a_room_has_more_than_a_page_of_messages()
    {
        Execute(app, $"""
            INSERT INTO rooms (name, type, creator_id, created_at, updated_at) VALUES ('Paging', 'Rooms::Closed', {David}, '2026-03-02 16:00:00', '2026-03-02 16:00:00');
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {MessagePages.PageSize - 1})
            INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at)
            SELECT (SELECT MAX(id) FROM rooms), {David}, 'paging-' || i, '2026-03-02 16:00:00', '2026-03-02 16:00:00' FROM n;
            """);
        var room = (long)app.Scalar("SELECT MAX(id) FROM rooms")!;

        var (paged, statements) = await app.Database.ReadAsync(session => Recording(session, () => MessagePages.IsPaged(session, room)), Cancellation);
        Assert.False(paged);
        Assert.NotEmpty(statements);
        Assert.DoesNotContain(statements, sql => sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));

        Execute(app, $"INSERT INTO messages (room_id, creator_id, client_message_id, created_at, updated_at) VALUES ({room}, {David}, 'paging-next', '2026-03-02 16:00:00', '2026-03-02 16:00:00')");
        (paged, statements) = await app.Database.ReadAsync(session => Recording(session, () => MessagePages.IsPaged(session, room)), Cancellation);
        Assert.True(paged);
        Assert.DoesNotContain(statements, sql => sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
    }

    // The reference stubs PAGE_SIZE to 2 to see the newest page; the port's page is 40, so this
    // keeps the order: touched out of order, the messages still come back in creation order.
    [Fact]
    public async Task Messages_updated_since_a_time_are_the_newest_page_of_them_in_creation_order()
    {
        var oldest = await app.Database.ReadAsync(session => MessagePages.FirstPage(session, Designers), Cancellation);
        var (first, second, third) = (oldest[0].Id, oldest[1].Id, oldest[2].Id);
        foreach (var (id, at) in new[] { (third, "16:01:00"), (first, "16:01:01"), (second, "16:01:02") })
        {
            Execute(app, $"UPDATE messages SET updated_at = '2026-03-02 {at}' WHERE id = {id}");
        }

        var since = new DateTimeOffset(2026, 3, 2, 16, 0, 30, TimeSpan.Zero);
        var updated = await app.Database.ReadAsync(session => MessagePages.PageUpdatedSince(session, Designers, since, []), Cancellation);

        Assert.Equal([first, second, third], updated.Select(message => message.Id));
    }

    [Fact]
    public async Task Find_the_room_with_exactly_the_same_users()
    {
        var group = await FindOrCreateDirectAsync(David, Kevin, Jason);

        Assert.Equal(DavidAndKevin, await FindOrCreateDirectAsync(Kevin, David));
        Assert.Equal(group, await FindOrCreateDirectAsync(Jason, Kevin, David));
        Assert.Equal([David, Jz], await MembersAsync(await FindOrCreateDirectAsync(David, Jz)));
        Assert.Equal([Jason, Kevin], await MembersAsync(await FindOrCreateDirectAsync(Jason, Kevin)));
    }

    [Fact]
    public async Task Finding_a_room_takes_the_same_queries_however_many_direct_rooms_the_account_has()
    {
        var davidAndJz = await FindOrCreateDirectAsync(David, Jz);
        var before = await FindDirectStatementsAsync(David, Jz);

        Execute(app, """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 4)
            INSERT INTO users (name, email_address, created_at, updated_at)
            SELECT 'Guest ' || i, 'guest' || i || '@example.com', '2026-03-02 16:00:00', '2026-03-02 16:00:00' FROM n;
            """);
        var guests = await app.Database.ReadAsync(session => session.Query("SELECT id FROM users WHERE email_address LIKE 'guest%@example.com' ORDER BY id", reader => reader.GetInt64(0)), Cancellation);
        foreach (var guest in guests)
        {
            await CreateDirectAsync(Jason, guest);
        }
        var kevinAndJz = await CreateDirectAsync(Kevin, Jz);

        var after = await FindDirectStatementsAsync(Kevin, Jz);

        Assert.Equal(davidAndJz, before.Room);
        Assert.Equal(kevinAndJz, after.Room);
        Assert.NotEmpty(before.Statements);
        Assert.Equal(before.Statements.Count, after.Statements.Count);
    }

    [Fact]
    public async Task Banned_users_are_visible_to_administrators_only()
    {
        Execute(app, $"UPDATE users SET status = 2 WHERE id = {Kevin}");

        var (toDavid, toJz) = await app.Database.ReadAsync(session => (
            Users.VisibleToOrderedWithRole(session, Users.Find(session, David)!, UserRole.Member),
            Users.VisibleToOrderedWithRole(session, Users.Find(session, Jz)!, UserRole.Member)), Cancellation);

        Assert.Contains(toDavid, user => user.Id == Kevin);
        Assert.DoesNotContain(toJz, user => user.Id == Kevin);
    }

    // `Rooms::Direct.find_or_create_for(users)`, as Rooms::DirectsController#create runs it.
    Task<long> FindOrCreateDirectAsync(params long[] users) =>
        app.Database.WriteAsync(tx =>
            (Rooms.FindDirectFor(tx.Session, users) ?? Rooms.CreateFor(tx.Session, RoomType.Direct, null, David, users, app.Now)).Id, Cancellation);

    // `Rooms::Direct.create_for({}, users:)`
    Task<long> CreateDirectAsync(params long[] users) =>
        app.Database.WriteAsync(tx => Rooms.CreateFor(tx.Session, RoomType.Direct, null, David, users, app.Now).Id, Cancellation);

    // `queries_during { Rooms::Direct.find_or_create_for(User.where(id: users)) }`, on a room that exists.
    Task<(long? Room, List<string> Statements)> FindDirectStatementsAsync(params long[] users) =>
        app.Database.ReadAsync(session =>
        {
            var (room, statements) = Recording(session, () => Rooms.FindDirectFor(session, users));
            return (room?.Id, statements);
        }, Cancellation);

    async Task<List<long>> MembersAsync(long room) =>
        [.. (await app.Database.ReadAsync(session => Memberships.UserIdsInRoom(session, room), Cancellation)).Order()];
}
