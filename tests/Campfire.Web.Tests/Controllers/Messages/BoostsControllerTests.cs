using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.RichText.Html;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// reference/test/controllers/messages/boosts_controller_test.rb on the default parity seed:
/// David boosts his launch message in Designers, and <c>boosts(:first)</c> is his 👍 on it. Then
/// security properties asserted independently of the reference.
/// </summary>
public sealed partial class BoostsControllerTests : IDisposable
{
    const long designersRoom = 654632876;
    const long launch = 933434507;
    const string launchClientId = "4f384e0a-1ad0-57d7-8c1b-b1ee6797e852";
    const long davidsThumbsUp = 329428236;
    const string davidSession = "AxJs94fteQ5Autv2VrKsH68c";

    readonly MessagesApp messages = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Create()
    {
        var count = BoostCount();

        var response = await messages.SendAsync("POST", $"/messages/{launch}/boosts.turbo_stream", messages.SignedIn(davidSession), "boost[content]=Morning%21");

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://{MessagesApp.Host}/messages/{launch}/boosts", response.Headers.Location.ToString());
        Assert.Equal(count + 1, BoostCount());
        Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream == RoomMessagesStream());
    }

    [Fact]
    public async Task Destroy()
    {
        var count = BoostCount();

        var response = await messages.SendAsync("DELETE", $"/messages/{launch}/boosts/{davidsThumbsUp}.turbo_stream", messages.SignedIn(davidSession));

        Assert.Equal(204, response.Status);
        Assert.Equal(count - 1, BoostCount());
        Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream == RoomMessagesStream());
    }

    // Independent of the reference: what a member writes in a boost never reaches the broadcast
    // or the boosts frame as markup.
    [Theory]
    [InlineData("<script>x</script>")]
    [InlineData("<img src=x onerror=1>")]
    [InlineData("\"><svg onload=1>")]
    public async Task Boost_content_never_renders_as_markup(string content)
    {
        await messages.SendAsync("POST", $"/messages/{launch}/boosts", messages.SignedIn(davidSession), $"boost[content]={Uri.EscapeDataString(content)}");
        var frame = messages.SignedIn(davidSession, "text/html");
        frame["Turbo-Frame"] = $"boosting_message_{launchClientId}";
        var index = await messages.SendAsync("GET", $"/messages/{launch}/boosts", frame);
        var broadcast = Assert.Single(messages.Seams.Broadcasts);

        Assert.Equal(200, index.Status);
        foreach (var html in new[] { JsonNode.Parse(broadcast.Payload)!.GetValue<string>(), index.Body })
        {
            var markup = html.Replace("<template>", "", StringComparison.Ordinal).Replace("</template>", "", StringComparison.Ordinal);
            foreach (var element in HtmlParser.ParseFragment(markup).Descendants().OfType<HtmlElement>())
            {
                Assert.NotEqual("script", element.Name);
                Assert.NotEqual("svg", element.Name);
                Assert.DoesNotContain(element.Attributes, attribute => attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
            }
            Assert.Matches(EscapedContent(), html);
        }
    }

    long BoostCount() => (long)messages.Scalar($"SELECT COUNT(*) FROM boosts WHERE message_id = {launch}")!;

    // `[room, :messages]` for Designers, a closed room.
    static string RoomMessagesStream() =>
        $"{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"gid://campfire/Rooms::Closed/{designersRoom}")).TrimEnd('=')}:messages";

    public void Dispose() => messages.Dispose();

    [GeneratedRegex("&lt;(script|img|svg)|&quot;&gt;")]
    private static partial Regex EscapedContent();
}
