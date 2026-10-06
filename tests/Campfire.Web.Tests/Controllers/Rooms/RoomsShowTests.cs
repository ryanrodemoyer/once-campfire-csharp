using System.Text.Encodings.Web;

namespace Campfire.Web.Tests.Controllers.Rooms;

/// <summary>
/// reference/test/controllers/rooms_controller_test.rb's index and show cases, on the parity seed
/// with CSRF protection on. rooms#destroy is M07's. The link preview cases are independent
/// security assertions: a hand-written opengraph embed posted through the composer never reaches
/// the room page with an off-scheme or same-host image or link.
/// </summary>
public sealed class RoomsShowTests : IDisposable
{
    const string david = "AxJs94fteQ5Autv2VrKsH68c";
    const long hq = 201306877;
    const long newestRoom = 699448329;

    readonly Messages.MessagesApp app = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Index_redirects_to_the_users_last_room()
    {
        var response = await app.SendAsync("GET", "/rooms", app.SignedIn(david, "text/html"));

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://campfire.test/rooms/{newestRoom}", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Show_records_the_last_room_visited_in_a_cookie()
    {
        var response = await app.SendAsync("GET", $"/rooms/{newestRoom}", app.SignedIn(david, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.Contains(response.Headers.SetCookie, cookie => cookie!.StartsWith($"last_room={newestRoom};", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Show_renders_a_link_preview_written_by_hand_without_its_off_scheme_image_and_link()
    {
        var body = await PostThenShowAsync("javascript:alert(1)", "data:image/svg+xml;base64,PHN2Zy8+", "hand-written-preview");

        Assert.DoesNotContain("javascript:alert", body, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image/svg", body, StringComparison.Ordinal);
        Assert.Contains("Free cookies", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_renders_a_link_preview_written_by_hand_without_its_image_pointed_at_this_Campfire()
    {
        var ownUrl = $"http://campfire.test/rooms/{hq}";
        var body = await PostThenShowAsync(ownUrl, ownUrl, "same-host-preview");

        Assert.DoesNotContain($"<img src=\"{ownUrl}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"<a rel=\"noreferrer\" target=\"_blank\" href=\"{ownUrl}\"", body, StringComparison.Ordinal);
        Assert.Contains("Free cookies", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_renders_an_unfurled_link_preview()
    {
        var body = await PostThenShowAsync("https://example.com/page", "https://example.com/image.png", "unfurled-preview");

        Assert.Contains("<img src=\"https://example.com/image.png\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/page\"", body, StringComparison.Ordinal);
    }

    async Task<string> PostThenShowAsync(string href, string url, string clientMessageId)
    {
        var preview = "<div><action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" "
            + $"href=\"{href}\" url=\"{url}\" filename=\"Free cookies\" caption=\"Cookies here\"></action-text-attachment></div>";
        var form = $"message%5Bbody%5D={UrlEncoder.Default.Encode(preview)}&message%5Bclient_message_id%5D={clientMessageId}";
        var posted = await app.SendAsync("POST", $"/rooms/{hq}/messages", app.SignedIn(david), form);
        Assert.Equal(200, posted.Status);

        var shown = await app.SendAsync("GET", $"/rooms/{hq}", app.SignedIn(david, "text/html"));
        Assert.Equal(200, shown.Status);
        return shown.Body;
    }

    public void Dispose() => app.Dispose();
}
