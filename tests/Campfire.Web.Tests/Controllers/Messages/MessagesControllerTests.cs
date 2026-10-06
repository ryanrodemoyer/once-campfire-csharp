using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.RichText.Html;
using Campfire.Vectors;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// reference/test/controllers/messages_controller_test.rb's create, show, update and destroy
/// cases (index is M04's), on the default parity seed: Designers stands in for the watercooler
/// (David, an administrator, and Jason both post there; JZ is a member), and Archive holds the
/// bot with a webhook. Then security properties asserted independently of the reference.
/// </summary>
public sealed partial class MessagesControllerTests : IDisposable
{
    const long designersRoom = 654632876;
    const long archiveRoom = 699448327;
    const long david = 127326141;
    const long jason = 149087659;
    const string davidSession = "AxJs94fteQ5Autv2VrKsH68c";
    const string jzSession = "JzSessionToken0000000001";

    static readonly string[] Fixtures =
    [
        "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
            $"VALUES (900002, 773523953, '{jzSession}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
    ];

    readonly MessagesApp messages = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero), Fixtures);

    [Fact]
    public async Task Get_renders_a_single_message_belonging_to_the_user()
    {
        var message = FirstMessageBy(david);

        var response = await messages.SendAsync("GET", $"/rooms/{designersRoom}/messages/{message}", messages.SignedIn(davidSession, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.Contains($"data-message-id=\"{message}\"", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_message_broadcasts_the_message_to_the_room()
    {
        var response = await Post(designersRoom, "message[body]=New+one&message[client_message_id]=999");

        Assert.Equal(200, response.Status);
        var id = (long)messages.Scalar("SELECT id FROM messages WHERE client_message_id = '999'")!;
        var broadcast = Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream.EndsWith(":messages", StringComparison.Ordinal));
        var html = JsonNode.Parse(broadcast.Payload)!.GetValue<string>();
        Assert.StartsWith($"<turbo-stream action=\"append\" target=\"messages_rooms_closed_{designersRoom}\"><template>", html, StringComparison.Ordinal);
        Assert.Matches(MessageBodyWithNewOne(), html);
        Assert.Contains($"title=\"Copy link\" aria-label=\"Copy link\" data-controller=\"copy-to-clipboard\" data-action=\"copy-to-clipboard#copy\" data-copy-to-clipboard-success-class=\"btn--success\" data-copy-to-clipboard-content-value=\"http://{MessagesApp.Host}/rooms/{designersRoom}/@{id}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_message_broadcasts_unread_room_to_each_member()
    {
        foreach (var member in RoomMembers(designersRoom))
        {
            messages.Seams.Clear();
            await Post(designersRoom, $"message[body]=New+one+{member}&message[client_message_id]={member}");

            Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream == $"user_{member}_unreads");
        }
    }

    [Fact]
    public async Task Creating_a_message_doesnt_broadcast_unread_room_to_non_members()
    {
        var members = RoomMembers(designersRoom);
        var outsiders = Query("SELECT id FROM users").Where(id => !members.Contains(id)).ToList();
        Assert.NotEmpty(outsiders);

        await Post(designersRoom, "message[body]=New+one&message[client_message_id]=999");

        foreach (var outsider in outsiders)
        {
            Assert.DoesNotContain(messages.Seams.Broadcasts, broadcast => broadcast.Stream == $"user_{outsider}_unreads");
        }
    }

    [Theory]
    [InlineData(david)]
    [InlineData(jason)]
    public async Task Update_updates_a_message_the_administrator_may_edit(long creator)
    {
        var message = FirstMessageBy(creator);

        var response = await messages.SendAsync("PUT", $"/rooms/{designersRoom}/messages/{message}", messages.SignedIn(davidSession), "message[body]=Updated+body");

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://{MessagesApp.Host}/rooms/{designersRoom}/messages/{message}", response.Headers.Location.ToString());
        Assert.Single(messages.Seams.Broadcasts, broadcast => JsonNode.Parse(broadcast.Payload)!.GetValue<string>().Contains("action=\"replace\"", StringComparison.Ordinal));
        Assert.Equal("Updated body", messages.Scalar($"SELECT body FROM message_search_index WHERE rowid = {message}"));
    }

    [Theory]
    [InlineData(david)]
    [InlineData(jason)]
    public async Task Destroy_destroys_a_message_the_administrator_may_edit(long creator)
    {
        var message = FirstMessageBy(creator);
        var count = MessageCount();

        var response = await messages.SendAsync("DELETE", $"/rooms/{designersRoom}/messages/{message}.turbo_stream", messages.SignedIn(davidSession));

        Assert.Equal(200, response.Status);
        Assert.Equal(count - 1, MessageCount());
        Assert.Single(messages.Seams.Broadcasts, broadcast => JsonNode.Parse(broadcast.Payload)!.GetValue<string>().Contains("action=\"remove\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_member_cant_update_a_message_belonging_to_another_user()
    {
        var message = FirstMessageBy(jason);

        var response = await messages.SendAsync("PUT", $"/rooms/{designersRoom}/messages/{message}", messages.SignedIn(jzSession), "message[body]=Updated+body");

        Assert.Equal(403, response.Status);
        Assert.Empty(messages.Seams.Events);
    }

    [Fact]
    public async Task A_member_cant_destroy_a_message_belonging_to_another_user()
    {
        var message = FirstMessageBy(jason);
        var count = MessageCount();

        var response = await messages.SendAsync("DELETE", $"/rooms/{designersRoom}/messages/{message}.turbo_stream", messages.SignedIn(jzSession));

        Assert.Equal(403, response.Status);
        Assert.Equal(count, MessageCount());
    }

    [Fact]
    public async Task Mentioning_a_bot_triggers_a_webhook()
    {
        var response = await Post(archiveRoom, $"message[body]={Uri.EscapeDataString($"<div>Hey {BenderMention()}</div>")}&message[client_message_id]=999");

        Assert.Equal(200, response.Status);
        var webhook = Assert.Single(messages.Seams.Jobs.OfType<WebhookJob>());
        Assert.Equal(394959859, webhook.BotId);
    }

    // Independent of the reference: markup a member posts never reaches the page, the broadcast
    // or the editor as script.
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<p onclick=\"alert(1)\">Hi</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("\"><script>alert(1)</script>")]
    public async Task Posted_markup_never_renders_as_script(string body)
    {
        var created = await Post(designersRoom, $"message[body]={Uri.EscapeDataString(body)}&message[client_message_id]=hostile");
        var id = (long)messages.Scalar("SELECT id FROM messages WHERE client_message_id = 'hostile'")!;
        var edit = await messages.SendAsync("GET", $"/rooms/{designersRoom}/messages/{id}/edit", messages.SignedIn(davidSession, "text/html"));
        var broadcasts = messages.Seams.Broadcasts.Select(broadcast => broadcast.Payload.StartsWith('"') ? JsonNode.Parse(broadcast.Payload)!.GetValue<string>() : broadcast.Payload);

        // The layout's own scripts are the app's; the page is what's inside <main>.
        var page = edit.Body[edit.Body.IndexOf("<main", StringComparison.Ordinal)..edit.Body.IndexOf("</main>", StringComparison.Ordinal)];
        Assert.Equal(200, edit.Status);
        foreach (var html in broadcasts.Append(created.Body).Append(page))
        {
            AssertInert(html);
        }
    }

    [Fact]
    public async Task A_create_with_an_attachment_waits_for_M10()
    {
        var body = "--b\r\nContent-Disposition: form-data; name=\"message[attachment]\"; filename=\"a.txt\"\r\nContent-Type: text/plain\r\n\r\nhi\r\n" +
            "--b\r\nContent-Disposition: form-data; name=\"message[client_message_id]\"\r\n\r\nupload\r\n--b--\r\n";

        var response = await messages.SendAsync("POST", $"/rooms/{designersRoom}/messages", messages.SignedIn(davidSession, "*/*"), body, "multipart/form-data; boundary=b");

        Assert.Equal(501, response.Status);
        Assert.Null(messages.Scalar("SELECT id FROM messages WHERE client_message_id = 'upload'"));
    }

    Task<Response> Post(long room, string form) =>
        messages.SendAsync("POST", $"/rooms/{room}/messages.turbo_stream", messages.SignedIn(davidSession), form);

    // `room.messages.where(creator:).first`
    long FirstMessageBy(long creator) =>
        (long)messages.Scalar($"SELECT id FROM messages WHERE room_id = {designersRoom} AND creator_id = {creator} ORDER BY id LIMIT 1")!;

    long MessageCount() => (long)messages.Scalar("SELECT COUNT(*) FROM messages")!;

    List<long> RoomMembers(long room) => Query($"SELECT user_id FROM memberships WHERE room_id = {room}");

    List<long> Query(string sql)
    {
        using var connection = messages.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    // The reference's own mention of Bender, as Lexxy posts it (from Vectors/writes.json).
    static string BenderMention()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/writes.json")))!;
        var request = vectors["cases"]!.AsArray().Single(sample => sample!["name"]!.GetValue<string>() == "administrator mentions bots")!["request"]!;
        var body = System.Web.HttpUtility.ParseQueryString(request["body"]!.GetValue<string>())["message[body]"]!;
        return MentionAttachment().Match(body).Value;
    }

    // Parsed as a browser would, so text inside attribute values can't pass for markup; a turbo
    // stream's template is parsed as the markup it inserts.
    static void AssertInert(string html)
    {
        var markup = html.Replace("<template>", "", StringComparison.Ordinal).Replace("</template>", "", StringComparison.Ordinal);
        foreach (var element in HtmlParser.ParseFragment(markup).Descendants().OfType<HtmlElement>())
        {
            Assert.DoesNotContain(element.Name, ScriptCapableElements);
            foreach (var attribute in element.Attributes)
            {
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"<{element.Name} {attribute.Name}>");
                if (UrlAttributes.Contains(attribute.Name))
                {
                    Assert.DoesNotMatch(ScriptUrl(), attribute.Value);
                }
            }
        }
    }

    static readonly string[] ScriptCapableElements = ["script", "iframe", "object", "embed", "frame", "frameset", "base"];

    static readonly string[] UrlAttributes = ["href", "src", "action", "poster", "formaction"];

    public void Dispose() => messages.Dispose();

    [GeneratedRegex("<div class=\"message__body\">(?:(?!</turbo-frame>).)*New one", RegexOptions.Singleline)]
    private static partial Regex MessageBodyWithNewOne();

    [GeneratedRegex("<action-text-attachment[^>]*></action-text-attachment>")]
    private static partial Regex MentionAttachment();

    [GeneratedRegex(@"^\s*(javascript|vbscript|data):", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrl();
}
