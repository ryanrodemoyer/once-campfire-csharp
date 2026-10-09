using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Web.Helpers.Rails;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.Rooms;

/// <summary>
/// reference/test/controllers/rooms/{opens,closeds,directs,involvements}_controller_test.rb and
/// rooms_controller_test.rb's destroy cases, on the parity seed with CSRF protection on. The
/// fixtures map to the seed's rooms: hq to HQ, pets and watercooler to All Pets (open, David
/// involved in everything), designers to Designers (closed), david_and_jason, david_and_kevin and
/// bender_and_kevin to the direct rooms of those users.
/// </summary>
public sealed class RoomSettingsControllerTests : IDisposable
{
    const string david = "AxJs94fteQ5Autv2VrKsH68c";
    const string kevin = "KevinSessionToken0000001";
    const string jz = "JzSessionToken0000000001";

    const long davidId = 127326141;
    const long jasonId = 149087659;
    const long benderId = 394959859;
    const long kevinId = 712064548;
    const long jzId = 773523953;

    const long pets = 104393281;
    const long davidAndJason = 186869642;
    const long hq = 201306877;
    const long benderAndKevin = 340026324;
    const long allTalk = 486777696;
    const long designers = 654632876;
    const long davidAndKevin = 699448325;

    static readonly string[] Fixtures =
    [
        "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) "
            + $"VALUES (900001, {kevinId}, '{kevin}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) "
            + $"VALUES (900002, {jzId}, '{jz}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
    ];

    readonly MessagesApp app = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero), Fixtures);

    // Rooms::OpensControllerTest

    [Fact]
    public async Task Opens_show_redirects_to_get_general_show()
    {
        var response = await GetAsync(david, $"/rooms/opens/{hq}");

        AssertRedirectedTo($"/rooms/{hq}", response);
    }

    [Fact]
    public async Task Opens_new()
    {
        Assert.Equal(200, (await GetAsync(david, "/rooms/opens/new")).Status);
    }

    [Fact]
    public async Task Opens_create()
    {
        var response = await SendAsync(david, "POST", "/rooms/opens", "room%5Bname%5D=My+New+Room");

        var room = NewestRoom();
        Assert.Single(BroadcastsTo("rooms"));
        Assert.Equal(ActiveUserCount(), MembershipCount(room));
        AssertRedirectedTo($"/rooms/{room}", response);
    }

    [Fact]
    public async Task Opens_create_forbidden_by_non_admin_when_account_restricts_creation_to_admins()
    {
        RestrictRoomCreationToAdministrators();

        var response = await SendAsync(jz, "POST", "/rooms/opens", "room%5Bname%5D=My+New+Room");

        Assert.Equal(403, response.Status);
    }

    [Fact]
    public async Task Opens_only_admins_or_creators_can_update()
    {
        var response = await SendAsync(jz, "PUT", $"/rooms/opens/{hq}", "room%5Bname%5D=New+Name");

        Assert.Equal(403, response.Status);
        Assert.Empty(BroadcastsTo("rooms"));
        Assert.Equal("HQ", RoomName(hq));
    }

    [Fact]
    public async Task Opens_update()
    {
        var response = await SendAsync(david, "PUT", $"/rooms/opens/{pets}", "room%5Bname%5D=New+Name");

        Assert.Single(BroadcastsTo("rooms"));
        AssertRedirectedTo($"/rooms/{pets}", response);
        Assert.Equal("New Name", RoomName(pets));
    }

    [Fact]
    public async Task Opens_update_a_closed_room_to_be_open()
    {
        await SendAsync(david, "PUT", $"/rooms/opens/{designers}", "room%5Bname%5D=Doesn%27t+matter");

        Assert.Equal("Rooms::Open", RoomType(designers));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM users WHERE status = 0 AND id NOT IN (SELECT user_id FROM memberships WHERE room_id = {designers})"));
    }

    [Fact]
    public async Task Opens_a_direct_room_cant_be_promoted_to_open_by_its_creator()
    {
        await SendAsync(kevin, "PUT", $"/rooms/opens/{benderAndKevin}", "room%5Bname%5D=Watercooler");

        Assert.Equal("Rooms::Direct", RoomType(benderAndKevin));
        Assert.Equal([benderId, kevinId], UserIds(benderAndKevin));
    }

    [Fact]
    public async Task Opens_a_direct_room_cant_be_promoted_to_open_by_an_administrator_either()
    {
        await SendAsync(david, "PUT", $"/rooms/opens/{davidAndKevin}", "room%5Bname%5D=Watercooler");

        Assert.Equal("Rooms::Direct", RoomType(davidAndKevin));
        Assert.Equal([davidId, kevinId], UserIds(davidAndKevin));
    }

    // Rooms::ClosedsControllerTest

    [Fact]
    public async Task Closeds_show_redirects_to_get_general_show()
    {
        var response = await GetAsync(david, $"/rooms/closeds/{designers}");

        AssertRedirectedTo($"/rooms/{designers}", response);
    }

    [Fact]
    public async Task Closeds_new()
    {
        Assert.Equal(200, (await GetAsync(david, "/rooms/closeds/new")).Status);
    }

    [Fact]
    public async Task Closeds_create()
    {
        var response = await SendAsync(david, "POST", "/rooms/closeds",
            $"room%5Bname%5D=My+New+Room&user_ids%5B%5D={davidId}&user_ids%5B%5D={kevinId}&user_ids%5B%5D={jasonId}");

        foreach (var userId in new[] { davidId, kevinId, jasonId })
        {
            Assert.Single(BroadcastsTo(UserRooms(userId)));
        }
        var room = NewestRoom();
        Assert.Equal(3L, MembershipCount(room));
        AssertRedirectedTo($"/rooms/{room}", response);
    }

    [Fact]
    public async Task Closeds_create_forbidden_by_non_admin_when_account_restricts_creation_to_admins()
    {
        RestrictRoomCreationToAdministrators();

        var response = await SendAsync(jz, "POST", "/rooms/closeds", $"room%5Bname%5D=My+New+Room&user_ids%5B%5D={davidId}&user_ids%5B%5D={kevinId}");

        Assert.Equal(403, response.Status);
    }

    [Fact]
    public async Task Closeds_update_with_membership_revisions()
    {
        var members = UserIds(designers);
        var kept = string.Join('&', members.Where(id => id != jasonId).Select(id => $"user_ids%5B%5D={id}"));

        var response = await SendAsync(david, "PUT", $"/rooms/closeds/{designers}", $"room%5Bname%5D=New+Name&{kept}");

        Assert.Equal(members.Count - 1, UserIds(designers).Count);
        AssertRedirectedTo($"/rooms/{designers}", response);
        Assert.Equal("New Name", RoomName(designers));
        Assert.Contains(app.Seams.Disconnects, disconnect => disconnect.UserId == jasonId && disconnect.Reconnect);
    }

    [Fact]
    public async Task Closeds_update_an_open_room_to_be_closed()
    {
        await SendAsync(david, "PUT", $"/rooms/closeds/{pets}", $"room%5Bname%5D=Doesn%27t+matter&user_ids%5B%5D={davidId}&user_ids%5B%5D={jasonId}");

        Assert.Equal(2L, MembershipCount(pets));
        Assert.Equal("Rooms::Closed", RoomType(pets));
    }

    [Fact]
    public async Task Closeds_only_admins_or_creators_can_update()
    {
        var response = await SendAsync(jz, "PUT", $"/rooms/closeds/{designers}", "room%5Bname%5D=New+Name");

        Assert.Equal(403, response.Status);
        Assert.Empty(app.Seams.Broadcasts);
        Assert.Equal("Designers", RoomName(designers));
    }

    [Fact]
    public async Task Closeds_a_direct_room_cant_be_converted_to_closed_and_have_its_participants_revised()
    {
        await SendAsync(kevin, "PUT", $"/rooms/closeds/{benderAndKevin}", $"room%5Bname%5D=Watercooler&user_ids%5B%5D={kevinId}&user_ids%5B%5D={jzId}");

        Assert.Equal("Rooms::Direct", RoomType(benderAndKevin));
        Assert.Equal([benderId, kevinId], UserIds(benderAndKevin));
    }

    [Fact]
    public async Task Closeds_remove_yourself()
    {
        var before = RoomCountFor(davidId);
        var others = string.Join('&', UserIds(designers).Where(id => id != davidId).Select(id => $"user_ids%5B%5D={id}"));

        var response = await SendAsync(david, "PUT", $"/rooms/closeds/{designers}", $"room%5Bname%5D=Designers&{others}");
        AssertRedirectedTo($"/rooms/{designers}", response);
        AssertRedirectedTo("/", await GetAsync(david, $"/rooms/{designers}"));

        Assert.Equal(before - 1, RoomCountFor(davidId));
    }

    // Rooms::DirectsControllerTest

    [Fact]
    public async Task Directs_create()
    {
        var response = await SendAsync(david, "POST", "/rooms/directs", $"user_ids%5B%5D={jzId}");

        var room = NewestRoom();
        AssertRedirectedTo($"/rooms/{room}", response);
        Assert.Equal([davidId, jzId], UserIds(room));
    }

    [Fact]
    public async Task Directs_create_only_once_per_user_set()
    {
        var before = RoomCount();

        await SendAsync(david, "POST", "/rooms/directs", $"user_ids%5B%5D={jzId}");
        await SendAsync(david, "POST", "/rooms/directs", $"user_ids%5B%5D={jzId}");

        Assert.Equal(before + 1, RoomCount());
    }

    [Fact]
    public async Task Directs_destroy_only_allowed_for_all_room_users()
    {
        var before = RoomCount();

        var response = await SendAsync(kevin, "DELETE", $"/rooms/directs/{davidAndKevin}");

        AssertRedirectedTo("/", response);
        Assert.Equal(before - 1, RoomCount());
    }

    [Theory]
    [InlineData(kevin, designers)] // a closed room the member didn't create
    [InlineData(kevin, hq)] // an open room the member didn't create
    [InlineData(jz, davidAndKevin)] // a room the member isn't in at all
    public async Task Directs_destroy_cant_reach_the_room(string sessionToken, long roomId)
    {
        var before = RoomCount();

        await SendAsync(sessionToken, "DELETE", $"/rooms/directs/{roomId}");

        Assert.Equal(before, RoomCount());
        Assert.NotNull(RoomType(roomId));
    }

    // Rooms::InvolvementsControllerTest

    [Fact]
    public async Task Involvements_show()
    {
        Assert.Equal(200, (await GetAsync(david, $"/rooms/{designers}/involvement")).Status);
    }

    [Fact]
    public async Task Involvements_update_sends_turbo_update_when_becoming_visible_and_when_going_invisible()
    {
        Assert.Equal("everything", Involvement(davidId, pets));

        var response = await SendAsync(david, "PUT", $"/rooms/{pets}/involvement?involvement=invisible");
        AssertRedirectedTo($"/rooms/{pets}/involvement", response);
        Assert.Equal("invisible", Involvement(davidId, pets));
        Assert.Contains("action=\\\"remove\\\"", Assert.Single(BroadcastsTo(UserRooms(davidId))).Payload, StringComparison.Ordinal);

        app.Seams.Clear();
        response = await SendAsync(david, "PUT", $"/rooms/{pets}/involvement?involvement=everything");
        AssertRedirectedTo($"/rooms/{pets}/involvement", response);
        Assert.Equal("everything", Involvement(davidId, pets));
        Assert.Contains("action=\\\"prepend\\\"", Assert.Single(BroadcastsTo(UserRooms(davidId))).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Involvements_updating_does_not_send_turbo_update_changing_visible_states()
    {
        var response = await SendAsync(david, "PUT", $"/rooms/{pets}/involvement?involvement=mentions");

        AssertRedirectedTo($"/rooms/{pets}/involvement", response);
        Assert.Equal("mentions", Involvement(davidId, pets));
        Assert.Empty(BroadcastsTo(UserRooms(davidId)));
    }

    [Fact]
    public async Task Involvements_updating_does_not_send_turbo_update_for_direct_rooms()
    {
        Assert.Equal("everything", Involvement(davidId, davidAndJason));

        var response = await SendAsync(david, "PUT", $"/rooms/{davidAndJason}/involvement?involvement=nothing");

        AssertRedirectedTo($"/rooms/{davidAndJason}/involvement", response);
        Assert.Equal("nothing", Involvement(davidId, davidAndJason));
        Assert.Empty(BroadcastsTo(UserRooms(davidId)));
    }

    [Fact]
    public async Task Involvements_a_non_admin_can_update_their_room_involvement()
    {
        Assert.Equal("everything", Involvement(jzId, designers));

        var response = await SendAsync(jz, "PUT", $"/rooms/{designers}/involvement?involvement=mentions");

        AssertRedirectedTo($"/rooms/{designers}/involvement", response);
        Assert.Equal("mentions", Involvement(jzId, designers));
    }

    // RoomsControllerTest's destroy cases. Designers holds attachments, whose purge is M10's, so
    // All Talk stands in for it.

    [Fact]
    public async Task Destroy()
    {
        var before = RoomCount();

        await SendAsync(david, "DELETE", $"/rooms/{allTalk}");

        Assert.Single(BroadcastsTo("rooms"));
        Assert.Equal(before - 1, RoomCount());
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM messages WHERE room_id = {allTalk}"));
        Assert.Equal(0L, MembershipCount(allTalk));
    }

    [Fact]
    public async Task Destroy_only_allowed_for_creators_or_those_who_can_administer()
    {
        var before = RoomCount();

        var response = await SendAsync(jz, "DELETE", $"/rooms/{hq}");
        Assert.Equal(403, response.Status);
        Assert.Equal(before, RoomCount());

        app.Scalar($"UPDATE rooms SET creator_id = {jzId} WHERE id = {hq}");
        await SendAsync(jz, "DELETE", $"/rooms/{hq}");
        Assert.Equal(before - 1, RoomCount());
    }

    // Independent of the reference: a room's name is text wherever the settings render it.

    [Fact]
    public async Task A_room_name_never_renders_as_markup()
    {
        var response = await SendAsync(kevin, "POST", "/rooms/opens", "room%5Bname%5D=%3Cimg+src%3Dx+onerror%3Dalert(1)%3E");
        var room = NewestRoom();
        AssertRedirectedTo($"/rooms/{room}", response);

        var broadcast = JsonNode.Parse(Assert.Single(BroadcastsTo("rooms")).Payload)!.GetValue<string>();
        var edit = (await GetAsync(kevin, $"/rooms/opens/{room}/edit")).Body;

        foreach (var html in new[] { broadcast, edit })
        {
            Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<img src=\"x\"", html, StringComparison.Ordinal);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        }
    }

    Task<Response> GetAsync(string sessionToken, string path) => app.SendAsync("GET", path, app.SignedIn(sessionToken, "text/html, application/xhtml+xml"));

    Task<Response> SendAsync(string sessionToken, string method, string path, string? form = null)
    {
        app.Seams.Clear();
        return app.SendAsync(method, path, app.SignedIn(sessionToken), form ?? "");
    }

    static void AssertRedirectedTo(string path, Response response)
    {
        Assert.Equal(302, response.Status);
        Assert.Equal($"http://{MessagesApp.Host}{path}", response.Headers.Location.ToString());
    }

    IEnumerable<Data.Events.Broadcast> BroadcastsTo(string stream) => app.Seams.Broadcasts.Where(broadcast => broadcast.Stream == stream);

    static string UserRooms(long userId) => $"{RecordIdentifier.GidParam("User", userId)}:rooms";

    void RestrictRoomCreationToAdministrators() =>
        app.Scalar("UPDATE accounts SET settings = '{\"restrict_room_creation_to_administrators\":true}'");

    long NewestRoom() => (long)app.Scalar("SELECT MAX(id) FROM rooms")!;

    long RoomCount() => (long)app.Scalar("SELECT COUNT(*) FROM rooms")!;

    long RoomCountFor(long userId) => (long)app.Scalar($"SELECT COUNT(*) FROM memberships WHERE user_id = {userId}")!;

    long MembershipCount(long roomId) => (long)app.Scalar($"SELECT COUNT(*) FROM memberships WHERE room_id = {roomId}")!;

    long ActiveUserCount() => (long)app.Scalar("SELECT COUNT(*) FROM users WHERE status = 0")!;

    string? RoomName(long roomId) => app.Scalar($"SELECT name FROM rooms WHERE id = {roomId}") as string;

    string? RoomType(long roomId) => app.Scalar($"SELECT type FROM rooms WHERE id = {roomId}") as string;

    string? Involvement(long userId, long roomId) =>
        app.Scalar($"SELECT involvement FROM memberships WHERE user_id = {userId} AND room_id = {roomId}") as string;

    List<long> UserIds(long roomId)
    {
        using var connection = app.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT user_id FROM memberships WHERE room_id = {roomId.ToString(CultureInfo.InvariantCulture)} ORDER BY user_id";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public void Dispose() => app.Dispose();
}
