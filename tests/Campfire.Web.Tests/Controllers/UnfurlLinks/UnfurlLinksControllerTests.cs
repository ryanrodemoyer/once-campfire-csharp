using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Jobs.OpenGraph;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.UnfurlLinks;

/// <summary>
/// Port of reference/test/controllers/unfurl_links_controller_test.rb,
/// verifying unfurl_link#create, opengraph metadata extraction, private-network rejection,
/// authentication, and CSRF protection.
/// </summary>
public sealed class UnfurlLinksControllerTests : IAsyncDisposable
{
    const string david = "DavidSessionToken0000001";
    const long davidId = 127326141;

    readonly MessagesApp app;
    readonly ScriptedResolver resolver;
    readonly UnfurlServer server;
    readonly OpenGraphFetch fetch;

    public UnfurlLinksControllerTests()
    {
        app = new MessagesApp(
            new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
            [
                "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                    $"VALUES (900001, {davidId}, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            ]);
        resolver = new ScriptedResolver();
        server = new UnfurlServer();
        fetch = server.CreateFetch(resolver);
        app.App.OpenGraphFetch = fetch;
    }

    void StubSuccessfulRequest(
        string host = "www.example.com",
        string path = "/",
        string ogUrl = "https://example.com",
        string title = "Hey!",
        string description = "desc..",
        string image = "https://example.com/image.png")
    {
        var html = $"<html><head><meta property=\"og:url\" content=\"{ogUrl}\"><meta property=\"og:title\" content=\"{title}\"><meta property=\"og:description\" content=\"{description}\"><meta property=\"og:image\" content=\"{image}\"></head></html>";
        server.AddHtml(host, path, html);
        server.AddImage("example.com", "/image.png", "image/png");
    }

    [Fact]
    public async Task Create_returns_opengraph_metadata_as_json()
    {
        StubSuccessfulRequest();

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=https%3A%2F%2Fwww.example.com");

        Assert.Equal(200, response.Status);
        Assert.StartsWith("application/json", response.Headers["Content-Type"].ToString(), StringComparison.OrdinalIgnoreCase);

        var json = JsonNode.Parse(response.Body)!.AsObject();
        Assert.Equal("Hey!", json["title"]!.GetValue<string>());
        Assert.Equal("https://example.com", json["url"]!.GetValue<string>());
        Assert.Equal("https://example.com/image.png", json["image"]!.GetValue<string>());
        Assert.Equal("desc..", json["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_with_json_body_returns_opengraph_metadata()
    {
        StubSuccessfulRequest();

        var payload = JsonSerializer.Serialize(new { url = "https://www.example.com" });
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david, "application/json"),
            payload, "application/json");

        Assert.Equal(200, response.Status);
        var json = JsonNode.Parse(response.Body)!.AsObject();
        Assert.Equal("Hey!", json["title"]!.GetValue<string>());
        Assert.Equal("https://example.com", json["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_strips_markup_from_title_and_description()
    {
        var entityEncodedImageTag = "&#x3c;&#x69;&#x6d;&#x67;&#x20;&#x73;&#x72;&#x63;&#x3d;&#x61;&#x20;&#x6f;&#x6e;&#x65;&#x72;&#x72;&#x6f;&#x72;&#x3d;&#x70;&#x72;&#x6f;&#x6d;&#x70;&#x74;&#x28;&#x31;&#x29;&#x3e;";
        StubSuccessfulRequest(title: $"{entityEncodedImageTag}Hey!", description: $"{entityEncodedImageTag}desc..");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=https%3A%2F%2Fwww.example.com");

        Assert.Equal(200, response.Status);
        var json = JsonNode.Parse(response.Body)!.AsObject();
        Assert.Equal("Hey!", json["title"]!.GetValue<string>());
        Assert.Equal("desc..", json["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_with_missing_opengraph_meta_tags_returns_no_content()
    {
        server.AddHtml("www.example.com", "/", "<html><head></head></html>");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=https%3A%2F%2Fwww.example.com");

        Assert.Equal(204, response.Status);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task Create_returns_no_content_when_title_and_description_are_only_markup_tag()
    {
        var imageTag = "<img src='x' onerror='alert(document.domain)'/>";
        var body = "<html><head>" +
            "<meta property=\"og:url\" content=\"https://example.com\">" +
            $"<meta property=\"og:title\" content=\"{imageTag}\">" +
            $"<meta property=\"og:description\" content=\"{imageTag}\">" +
            "<meta property=\"og:image\" content=\"https://example.com/image.png\">" +
            "</head></html>";
        server.AddHtml("www.example.com", "/", body);
        server.AddImage("example.com", "/image.png", "image/png");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=https%3A%2F%2Fwww.example.com");

        Assert.Equal(204, response.Status);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task Create_with_missing_or_blank_url_returns_bad_request()
    {
        // Empty url param
        var emptyUrlResponse = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=");
        Assert.Equal(400, emptyUrlResponse.Status);

        // Missing url param completely
        var missingUrlResponse = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "");
        Assert.Equal(400, missingUrlResponse.Status);

        // Whitespace url param
        var whitespaceUrlResponse = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=%20%20%20");
        Assert.Equal(400, whitespaceUrlResponse.Status);
    }

    [Fact]
    public async Task Create_for_twitter()
    {
        server.AddHtml("fxtwitter.com", "/dhh/status/834146806594433025",
            "<html><head><meta property=\"og:url\" content=\"https://twitter.com/dhh/status/834146806594433025\"><meta property=\"og:title\" content=\"Hey!\"><meta property=\"og:description\" content=\"desc..\"><meta property=\"og:image\" content=\"https://example.com/image.png\"></head></html>");
        server.AddImage("example.com", "/image.png", "image/png");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=https%3A%2F%2Ftwitter.com%2Fdhh%2Fstatus%2F834146806594433025");

        Assert.Equal(200, response.Status);
        var json = JsonNode.Parse(response.Body)!.AsObject();
        Assert.Equal("Hey!", json["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_for_x()
    {
        server.AddHtml("fxtwitter.com", "/dhh/status/834146806594433025",
            "<html><head><meta property=\"og:url\" content=\"https://x.com/dhh/status/834146806594433025\"><meta property=\"og:title\" content=\"Hey!\"><meta property=\"og:description\" content=\"desc..\"><meta property=\"og:image\" content=\"https://example.com/image.png\"></head></html>");
        server.AddImage("example.com", "/image.png", "image/png");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=https%3A%2F%2Fx.com%2Fdhh%2Fstatus%2F834146806594433025");

        Assert.Equal(200, response.Status);
        var json = JsonNode.Parse(response.Body)!.AsObject();
        Assert.Equal("Hey!", json["title"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:3000/")]
    public async Task Create_rejects_private_network_addresses(string privateUrl)
    {
        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            $"url={UrlEncoder.Default.Encode(privateUrl)}");

        Assert.Equal(204, response.Status);
        Assert.Empty(response.Body);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Create_rejects_hostname_resolving_to_private_ip()
    {
        resolver.Add("metadata.internal", "10.0.0.5");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=http%3A%2F%2Fmetadata.internal%2Fsecret");

        Assert.Equal(204, response.Status);
        Assert.Empty(response.Body);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Create_rejects_redirect_to_private_network()
    {
        server.AddRedirect("www.example.com", "/redirect-private", "http://127.0.0.1/");

        var response = await app.SendAsync("POST", "/unfurl_link", app.SignedIn(david),
            "url=http%3A%2F%2Fwww.example.com%2Fredirect-private");

        Assert.Equal(204, response.Status);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task Create_requires_authentication()
    {
        var response = await app.SendAsync("POST", "/unfurl_link", new Dictionary<string, string>(),
            "url=https%3A%2F%2Fwww.example.com");

        Assert.Equal(302, response.Status);
        Assert.Contains("/session/new", response.Headers["Location"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_requires_csrf_token()
    {
        var headers = app.SignedIn(david);
        headers.Remove("X-CSRF-Token");

        var response = await app.SendAsync("POST", "/unfurl_link", headers,
            "url=https%3A%2F%2Fwww.example.com");

        Assert.Equal(422, response.Status);
    }

    public async ValueTask DisposeAsync()
    {
        fetch.Dispose();
        await server.DisposeAsync().ConfigureAwait(false);
        app.Dispose();
    }
}
