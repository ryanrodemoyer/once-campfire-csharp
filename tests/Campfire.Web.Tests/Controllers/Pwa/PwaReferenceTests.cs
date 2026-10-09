using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Templates;
using Campfire.Vectors;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.Pwa;

/// <summary>
/// Port of reference/test/controllers/qr_code_controller_test.rb and
/// reference/test/controllers/autocompletable/users_controller_test.rb, on the parity seed
/// with CSRF protection on. All Pets stands in for the fixture room Kevin is not a member of.
/// </summary>
public sealed class PwaReferenceTests : IDisposable
{
    const string david = "DavidSessionToken0000001";
    const string kevin = "KevinSessionToken0000002";
    const long davidId = 127326141;
    const long allPets = 104393281;

    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Pwa/Vectors/pwa.json")))!;

    readonly MessagesApp app = new(
        new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    // QrCodeControllerTest#show renders a QR code as a cacheable SVG image.
    [Fact]
    public async Task Show_renders_a_qr_code_as_a_cacheable_svg_image()
    {
        var id = RubyBase64("http://example.com");
        var response = await app.SendAsync("GET", $"/qr_code/{id}", app.SignedIn(david, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.Contains("image/svg+xml", response.Headers.ContentType.ToString(), StringComparison.Ordinal);
        Assert.Contains("max-age=31556952", response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
        Assert.Contains("public", response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
    }

    // Autocompletable::UsersControllerTest#search returns matching users.
    [Fact]
    public async Task Search_returns_matching_users()
    {
        var response = await app.SendAsync("GET", "/autocompletable/users.json?query=da", app.SignedIn(david, "application/json"));

        Assert.Equal(200, response.Status);
        Assert.Equal("David", JsonNode.Parse(response.Body)!.AsArray()[0]!["name"]!.GetValue<string>());
    }

    // Autocompletable::UsersControllerTest#search results escape HTML in names.
    // jbuilder runs h() and then JSON-encodes, so the parsed name still holds the entities.
    [Fact]
    public async Task Search_results_escape_html_in_names()
    {
        using (var connection = app.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET name = 'David <script>alert(123)</script>' WHERE id = $id";
            command.Parameters.AddWithValue("$id", davidId);
            command.ExecuteNonQuery();
        }

        var response = await app.SendAsync("GET", "/autocompletable/users.json?query=da", app.SignedIn(david, "application/json"));

        Assert.Equal(200, response.Status);
        Assert.Equal(
            "David &lt;script&gt;alert(123)&lt;/script&gt;",
            JsonNode.Parse(response.Body)!.AsArray()[0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("<script>", response.Body, StringComparison.Ordinal);
    }

    // Autocompletable::UsersControllerTest#room search returns matching users.
    [Fact]
    public async Task Room_search_returns_matching_users()
    {
        var response = await app.SendAsync("GET", $"/autocompletable/users.json?room_id={201306877}&query=da", app.SignedIn(david, "application/json"));

        Assert.Equal(200, response.Status);
        Assert.Equal("David", JsonNode.Parse(response.Body)!.AsArray()[0]!["name"]!.GetValue<string>());
    }

    // Autocompletable::UsersControllerTest#room search is scoped by membership.
    // Kevin is not a member of All Pets, so rooms.find raises RecordNotFound (404).
    [Fact]
    public async Task Room_search_is_scoped_by_membership()
    {
        var response = await app.SendAsync("GET", $"/autocompletable/users.json?room_id={allPets}&query=da", app.SignedIn(kevin, "application/json"));

        Assert.Equal(404, response.Status);
    }

    // The real autocomplete pill matches the stand-in M07 left under rooms/directs.
    [Fact]
    public void Autocompletable_user_template_matches_the_directs_stand_in()
    {
        var view = new View { Assets = ReferenceAssets.Bundle, Origin = new UrlBase("http", "campfire.test") };

        Assert.Equal(Render(view.UsersAutocompletablesTemplateStandIn), Render(view.UsersAutocompletablesTemplate));
    }

    static string Render(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Base64.urlsafe_encode64, the id the Rails test builds.
    static string RubyBase64(string text) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => app.Dispose();
}
