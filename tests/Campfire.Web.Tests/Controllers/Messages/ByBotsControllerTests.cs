using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Jobs.Webhooks;
using Campfire.RichText.Html;
using Campfire.Vectors;
using Campfire.Web.Controllers;
using Campfire.Web.Helpers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// The bot API and Bot::WebhookJob end to end, on the parity seed: ports of
/// reference/test/controllers/messages/by_bots_controller_test.rb's webhook cases and
/// reference/test/models/webhook_test.rb, against a local endpoint. ByBotsReplayTests holds the
/// rest against the reference.
/// </summary>
public sealed class ByBotsControllerTests : IDisposable
{
    const long bender = 394959859;
    const string benderKey = "394959859-BenderBot123";
    const string deployKey = "773523956-DeployBot456";
    const long archive = 699448327;

    static readonly Dictionary<string, string> Curl = new() { ["User-Agent"] = "curl/8.5.0", ["Accept"] = "*/*" };

    readonly MessagesApp messages = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_mention_delivers_the_message_to_the_bot_and_posts_its_text_reply()
    {
        await using var endpoint = new Endpoint("200 OK", "text/plain", "Hello back!");
        PointBendersWebhookAt(endpoint.Url);
        // The reference's mention of Bender, signed with the parity keys.
        var mention = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/by_bots.json")))!["cases"]!.AsArray()
            .Single(sample => sample!["name"]!.GetValue<string>() == "create mentioning a bot with a webhook")!["request"]!["body"]!.GetValue<string>();
        var created = await messages.SendAsync("POST", $"/rooms/{archive}/{deployKey}/messages", Curl, mention, "text/plain");
        var job = Assert.Single(messages.Seams.Jobs.OfType<WebhookJob>());
        messages.Seams.Clear();

        using var client = new WebhookClient();
        await Webhooks(client).PerformAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(201, created.Status);
        var payload = JsonNode.Parse(endpoint.Body!)!;
        Assert.Equal(job.MessageId, payload["message"]!["id"]!.GetValue<long>());
        Assert.Equal("Hey , deploy?", payload["message"]!["body"]!["plain"]!.GetValue<string>());
        Assert.Equal($"/rooms/{archive}/{benderKey}/messages", payload["room"]!["path"]!.GetValue<string>());
        Assert.Equal("Hello back!", LatestBody(bender));
        Assert.Contains(messages.Seams.Broadcasts, broadcast => broadcast.Stream.EndsWith(":messages", StringComparison.Ordinal) && broadcast.Payload.Contains("Hello back!", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_endpoint_that_doesnt_answer_in_time_gets_a_reply_saying_so()
    {
        await using var endpoint = new Endpoint("200 OK", "text/plain", "too late", delay: TimeSpan.FromSeconds(3));
        PointBendersWebhookAt(endpoint.Url);

        using var client = new WebhookClient(TimeSpan.FromSeconds(1));
        await Webhooks(client).PerformAsync(new WebhookJob(bender, 136976342), TestContext.Current.CancellationToken);

        Assert.Equal("Failed to respond within 1 seconds", LatestBody(bender));
    }

    [Fact]
    public async Task A_reply_without_a_content_type_posts_nothing()
    {
        await using var endpoint = new Endpoint("200 OK", null, "");
        PointBendersWebhookAt(endpoint.Url);
        var count = MessageCount();

        using var client = new WebhookClient();
        await Webhooks(client).PerformAsync(new WebhookJob(bender, 136976342), TestContext.Current.CancellationToken);

        Assert.Equal(count, MessageCount());
        Assert.Empty(messages.Seams.Events);
    }

    [Fact]
    public async Task A_bot_without_a_webhook_fails_the_job()
    {
        using var client = new WebhookClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Webhooks(client).PerformAsync(new WebhookJob(773523956, 136976342), TestContext.Current.CancellationToken));
    }

    // Independent of the reference: what a bot posts never reaches the room's broadcast or the
    // HTML index as live markup.
    [Theory]
    [InlineData("<script>alert(1)</script>hi")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\" onclick=\"x()\">here</a>")]
    [InlineData("\"><svg onload=alert(1)>")]
    public async Task Bot_bodies_never_render_as_markup(string body)
    {
        var created = await messages.SendAsync("POST", $"/rooms/{archive}/{benderKey}/messages", Curl, body, "text/plain");
        var index = await messages.SendAsync("GET", $"/rooms/{archive}/{benderKey}/messages.html", Curl);
        var broadcast = Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream.EndsWith(":messages", StringComparison.Ordinal));

        Assert.Equal(201, created.Status);
        Assert.Equal(200, index.Status);
        foreach (var html in new[] { JsonNode.Parse(broadcast.Payload)!.GetValue<string>(), index.Body })
        {
            var markup = html.Replace("<template>", "", StringComparison.Ordinal).Replace("</template>", "", StringComparison.Ordinal);
            foreach (var element in HtmlParser.ParseFragment(markup).Descendants().OfType<HtmlElement>())
            {
                Assert.NotEqual("script", element.Name);
                Assert.NotEqual("svg", element.Name);
                Assert.DoesNotContain(element.Attributes, attribute => attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(element.Attributes, attribute => attribute.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task The_JSON_escapes_markup_in_bodies()
    {
        await messages.SendAsync("POST", $"/rooms/{archive}/{benderKey}/messages", Curl, "<b>bold</b> & co", "text/plain");
        var index = await messages.SendAsync("GET", $"/rooms/{archive}/{benderKey}/messages", Curl);

        Assert.Equal("application/json; charset=utf-8", index.Headers.ContentType.ToString());
        Assert.DoesNotContain("<", index.Body, StringComparison.Ordinal);
        Assert.Contains("\\u003cb\\u003ebold\\u003c/b\\u003e \\u0026amp; co", index.Body, StringComparison.Ordinal);
    }

    // reference: a body that isn't UTF-8 fails `raw_request_body.blank?` or the rich text with a 500
    // as a raw body, and Rack's form parsing with a 400 as a form; nothing is written either way.
    [Theory]
    [InlineData("POST", "/rooms/486777696/394959859-BenderBot123/messages", "text/plain", 500)]
    [InlineData("PATCH", "/rooms/486777696/394959859-BenderBot123/messages/933434630", "text/plain", 500)]
    [InlineData("POST", "/rooms/486777696/394959859-BenderBot123/messages/136976342/boosts", "text/plain", 500)]
    [InlineData("POST", "/rooms/486777696/394959859-BenderBot123/messages", "application/x-www-form-urlencoded", 400)]
    [InlineData("POST", "/rooms/486777696/394959859-BenderBot123/messages/136976342/boosts", "application/x-www-form-urlencoded", 400)]
    public async Task A_body_that_isnt_UTF8_fails_as_in_the_reference(string method, string path, string contentType, int status)
    {
        var rows = (long)messages.Scalar("SELECT (SELECT COUNT(*) FROM messages) + (SELECT COUNT(*) FROM boosts) + (SELECT COUNT(*) FROM action_text_rich_texts)")!;
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Host = MessagesApp.Host;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream([0x63, 0x61, 0x66, 0xE9]);
        context.Request.ContentLength = 4;
        context.Response.Body = new MemoryStream();

        await messages.App.HandleAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(rows, (long)messages.Scalar("SELECT (SELECT COUNT(*) FROM messages) + (SELECT COUNT(*) FROM boosts) + (SELECT COUNT(*) FROM action_text_rich_texts)")!);
        Assert.Empty(messages.Seams.Events);
    }

    [Fact]
    public async Task A_text_reply_that_isnt_UTF8_fails_the_job()
    {
        var count = MessageCount();
        var (room, bot) = await messages.Database.ReadAsync(session => (Data.Queries.Rooms.Find(session, 486777696)!, Data.Queries.Users.Find(session, bender)!), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => new ByBotsWebhookReplies(messages.App).ReceiveTextAsync(room, bot, new WebhookTextReply([0x63, 0x61, 0x66, 0xE9]), TestContext.Current.CancellationToken));
        Assert.Equal(count, MessageCount());
    }

    BotWebhooks Webhooks(WebhookClient client) =>
        new(messages.Database, client, new ByBotsWebhookReplies(messages.App), session => new DatabaseAttachables(session, messages.Keys, messages.Now));

    void PointBendersWebhookAt(string url)
    {
        using var connection = messages.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE webhooks SET url = @url WHERE user_id = 394959859";
        command.Parameters.AddWithValue("@url", url);
        command.ExecuteNonQuery();
    }

    long MessageCount() => (long)messages.Scalar("SELECT COUNT(*) FROM messages")!;

    string? LatestBody(long creatorId) => messages.Scalar(
        $"SELECT body FROM action_text_rich_texts WHERE record_type = 'Message' AND record_id = (SELECT MAX(id) FROM messages WHERE creator_id = {creatorId})") as string;

    public void Dispose() => messages.Dispose();

    /// <summary>A webhook endpoint that answers every POST alike, after <c>delay</c>, and keeps the last body.</summary>
    sealed class Endpoint : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stopping = new();
        readonly Task serving;

        public Endpoint(string status, string? contentType, string body, TimeSpan? delay = null)
        {
            listener.Start();
            serving = ServeAsync(status, contentType, body, delay ?? TimeSpan.Zero);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/bender";

        public string? Body { get; private set; }

        async Task ServeAsync(string status, string? contentType, string body, TimeSpan delay)
        {
            try
            {
                while (true)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stopping.Token);
                    var stream = connection.GetStream();
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    var length = 0;
                    while (await reader.ReadLineAsync(stopping.Token) is { Length: > 0 } line)
                    {
                        if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                        {
                            length = int.Parse(line["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    var buffer = new char[length];
                    await reader.ReadBlockAsync(buffer, stopping.Token);
                    Body = new string(buffer);
                    await Task.Delay(delay, stopping.Token);
                    var content = Encoding.UTF8.GetBytes(body);
                    var head = $"HTTP/1.1 {status}\r\n{(contentType is null ? "" : $"Content-Type: {contentType}\r\n")}Content-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stopping.Token);
                    await stream.WriteAsync(content, stopping.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or IOException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            await serving;
            stopping.Dispose();
        }
    }
}
