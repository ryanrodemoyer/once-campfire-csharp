using System.Text.RegularExpressions;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.UsersSidebars;

/// <summary>
/// Port of <c>reference/test/controllers/users/sidebars_controller_test.rb</c>,
/// testing show, unread directs, and unread other rooms on the parity seed.
/// </summary>
public sealed class SidebarsReferenceTests : IDisposable
{
    const string davidToken = "DavidSessionToken0000001";
    const string jasonToken = "JasonSessionToken0000002";
    const long davidId = 127326141;
    const long jasonId = 149087659;

    const long davidAndJason = 186869642;
    const long watercooler = 104393281; // All Pets (open room)

    static readonly string[] Fixtures =
    [
        "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) "
            + $"VALUES (900001, {davidId}, '{davidToken}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) "
            + $"VALUES (900002, {jasonId}, '{jasonToken}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
    ];

    readonly MessagesApp app = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero), Fixtures);

    [Fact]
    public async Task Show()
    {
        var response = await app.SendAsync("GET", "/users/me/sidebar", app.SignedIn(davidToken, "text/html"));

        Assert.Equal(200, response.Status);
        // users(:david).rooms.opens.each do |room| assert_match /#{room.name}/, @response.body
        Assert.Contains("HQ", response.Body, StringComparison.Ordinal);
        Assert.Contains("All Pets", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unread_directs()
    {
        var form = "message%5Bbody%5D=Hello&message%5Bclient_message_id%5D=999";
        var posted = await app.SendAsync("POST", $"/rooms/{davidAndJason}/messages", app.SignedIn(jasonToken), form);
        Assert.Equal(200, posted.Status);

        var response = await app.SendAsync("GET", "/users/me/sidebar", app.SignedIn(davidToken, "text/html"));
        Assert.Equal(200, response.Status);

        long unreadDirects;
        using (var connection = app.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM memberships m JOIN rooms r ON m.room_id = r.id WHERE m.user_id = {davidId} AND r.type = '{Campfire.Data.Records.RoomTypes.DirectClassName}' AND m.unread_at IS NOT NULL AND m.involvement != 'invisible'";
            unreadDirects = (long)command.ExecuteScalar()!;
        }

        var directMatches = Regex.Matches(response.Body, @"<a class=""direct unread"" id=""list_rooms_direct_\d+""");
        Assert.Equal(unreadDirects, directMatches.Count);
        Assert.Contains($"id=\"list_rooms_direct_{davidAndJason}\"", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unread_other()
    {
        var form = "message%5Bbody%5D=Hello&message%5Bclient_message_id%5D=999";
        var posted = await app.SendAsync("POST", $"/rooms/{watercooler}/messages", app.SignedIn(jasonToken), form);
        Assert.Equal(200, posted.Status);

        var response = await app.SendAsync("GET", "/users/me/sidebar", app.SignedIn(davidToken, "text/html"));
        Assert.Equal(200, response.Status);

        long unreadOthers;
        using (var connection = app.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM memberships m JOIN rooms r ON m.room_id = r.id WHERE m.user_id = {davidId} AND r.type != '{Campfire.Data.Records.RoomTypes.DirectClassName}' AND m.unread_at IS NOT NULL AND m.involvement != 'invisible'";
            unreadOthers = (long)command.ExecuteScalar()!;
        }

        var sharedMatches = Regex.Matches(response.Body, @"<a id=""list_rooms_(open|closed)_\d+""[^>]*class=""[^""]*unread[^""]*""");
        Assert.Equal(unreadOthers, sharedMatches.Count);
        Assert.Contains($"id=\"list_rooms_open_{watercooler}\"", response.Body, StringComparison.Ordinal);
    }

    public void Dispose() => app.Dispose();
}
