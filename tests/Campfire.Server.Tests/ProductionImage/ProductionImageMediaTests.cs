using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Campfire.Server.Tests.Cli;
using Campfire.Vectors;

namespace Campfire.Server.Tests.ProductionImage;

/// <summary>
/// The production image (the root Dockerfile) on a fresh copy of the Rails fixtures, as
/// <c>run.sh</c> in this directory runs it. An uploaded image's thumbnail and a video's poster must
/// be the reference's bytes (vectors/storage/). The image's runtime stage carries the libvips and
/// ffmpeg those were made with, so this checks the packages are installed and wired, not only that
/// they run.
/// </summary>
public sealed partial class ProductionImageMediaTests : IDisposable
{
    const string email = "david@37signals.com";
    const string password = "secret123456";

    readonly HttpClient client = new(new HttpClientHandler { CookieContainer = new CookieContainer() });

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_uploaded_image_gets_the_reference_thumbnail()
    {
        Connect();
        var room = await SignInAsync();

        var path = await UploadAsync(room, "moon.jpg", "image/jpeg");

        await AssertReferenceBytesAsync(path, Variant("moon.jpg", "moon-thumb"));
    }

    [Fact]
    public async Task An_uploaded_video_gets_the_reference_poster()
    {
        Connect();
        var room = await SignInAsync();

        var path = await UploadAsync(room, "alpha-centuri.mov", "video/quicktime");

        await AssertReferenceBytesAsync(path, Variant("alpha-centuri.mov", "alpha-centuri-poster"));
    }

    /// <summary>Points the client at CAMPFIRE_PRODUCTION_URL, or skips when run.sh didn't start an image.</summary>
    void Connect()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CAMPFIRE_PRODUCTION_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            Assert.Skip("CAMPFIRE_PRODUCTION_URL isn't set: run tests/Campfire.Server.Tests/ProductionImage/run.sh");
        }
        client.BaseAddress = new Uri(baseUrl);
    }

    /// <summary>Signs in as the fixtures' David (CSRF on, as production), and returns the room the sign-in lands in.</summary>
    async Task<string> SignInAsync()
    {
        var token = Token(await GetPageAsync("/session/new"), AuthenticityToken());
        using var response = await client.PostAsync("/session", new FormUrlEncodedContent(
        [
            new("authenticity_token", token),
            new("email_address", email),
            new("password", password),
        ]), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var room = response.RequestMessage!.RequestUri!.AbsolutePath;
        Assert.StartsWith("/rooms/", room, StringComparison.Ordinal);
        return room;
    }

    /// <summary>Posts a message with the fixture as its attachment, and returns the representation path its reply links to.</summary>
    async Task<string> UploadAsync(string room, string fixture, string contentType)
    {
        var csrf = Token(await GetPageAsync(room), CsrfToken());
        using var content = new MultipartFormDataContent
        {
            { new StringContent(string.Empty), "message[body]" },
            { new StringContent(Guid.NewGuid().ToString("N")), "message[client_message_id]" },
            { new ByteArrayContent(Fixture(fixture)) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } }, "message[attachment]", fixture },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, room + "/messages") { Content = content };
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Accept.ParseAdd("text/vnd.turbo-stream.html");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadAsStringAsync(Cancel);
        var match = RepresentationPath().Match(reply);
        Assert.True(match.Success, "the reply links no representation (a thumbnail or a poster)");
        return match.Value;
    }

    /// <summary>Fetches a representation, following its redirect to the disk service, and compares it with the reference's file.</summary>
    async Task AssertReferenceBytesAsync(string path, StoredVariant variant)
    {
        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(variant.Blob.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StorageVectors.ReadFile(variant.File), await response.Content.ReadAsByteArrayAsync(Cancel));
    }

    async Task<string> GetPageAsync(string path)
    {
        using var response = await client.GetAsync(path, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    static string Token(string page, Regex pattern)
    {
        var match = pattern.Match(page);
        Assert.True(match.Success, $"the page has no {pattern}");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("name=\"authenticity_token\" value=\"([^\"]+)\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("name=\"csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex CsrfToken();

    [GeneratedRegex("/rails/active_storage/representations/redirect/[^\"'\\s<>]+")]
    private static partial Regex RepresentationPath();

    static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(TestRoot.RepositoryRoot, "reference", "test", "fixtures", "files", name));

    static StoredVariant Variant(string fixture, string label) =>
        StorageVectors.File.Messages.Single(m => m.Fixture == fixture).Variants!.Single(v => v.Label == label);

    public void Dispose() => client.Dispose();
}
