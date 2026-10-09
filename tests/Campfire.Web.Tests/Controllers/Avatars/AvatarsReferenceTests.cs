using System.Text.RegularExpressions;
using Campfire.Data.Records;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.Avatars;

/// <summary>
/// reference/test/controllers/users/avatars_controller_test.rb on the parity seed, signed in as
/// David with CSRF protection on, plus removing an avatar (which the reference doesn't test).
/// </summary>
public sealed partial class AvatarsReferenceTests : IDisposable
{
    const string david = "DavidSessionToken0000001";
    const string jason = "JasonSessionToken0000003";
    const long jasonId = 149087659;
    const long kevinId = 712064548;

    readonly MessagesApp app = new(
        new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
        [
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900001, 127326141, '{david}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900003, {jasonId}, '{jason}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        ]);

    public AvatarsReferenceTests() => AvatarsControllerTests.StageSeedStorage(app);

    [Fact]
    public async Task Show_initials()
    {
        var response = await app.SendAsync("GET", AvatarUrl(kevinId), app.SignedIn(david));

        Assert.Equal(200, response.Status);
        Assert.Equal("K", Text(response.Body));
    }

    [Fact]
    public async Task Show_image()
    {
        Assert.SkipUnless(LibVips.IsAvailable, "libvips isn't installed");
        await AttachAvatarAsync(kevinId, "moon.jpg", "image/jpeg");

        var response = await app.SendAsync("GET", AvatarUrl(kevinId), app.SignedIn(david));

        Assert.Equal(200, response.Status);
        Assert.Equal("image/webp", response.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task Show_initials_when_image_cannot_be_resized()
    {
        await AttachAvatarAsync(kevinId, "pixel.bmp", "image/bmp");

        var response = await app.SendAsync("GET", AvatarUrl(kevinId), app.SignedIn(david));

        Assert.Equal(200, response.Status);
        Assert.Equal("K", Text(response.Body));
    }

    [Fact]
    public async Task Show_image_with_invalid_token_responds_404()
    {
        var response = await app.SendAsync("GET", "/users/not-a-valid-token/avatar", app.SignedIn(david));

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Show_a_seed_upload()
    {
        var response = await app.SendAsync("GET", AvatarUrl(jasonId), app.SignedIn(david));

        Assert.Equal(200, response.Status);
        Assert.Equal("image/webp", response.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task Destroy_removes_the_signed_in_person_s_own_avatar()
    {
        var response = await app.SendAsync("DELETE", AvatarUrl(kevinId), app.SignedIn(jason));

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/users/me/profile", response.Headers.Location.ToString());
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'User' AND record_id = {jasonId}"));
        Assert.Equal("2026-03-02 16:00:00", app.Scalar($"SELECT updated_at FROM users WHERE id = {jasonId}"));
        Assert.Equal("J", Text((await app.SendAsync("GET", AvatarUrl(jasonId), app.SignedIn(david))).Body));
    }

    [Fact]
    public async Task Destroy_without_a_csrf_token_is_rejected()
    {
        var headers = app.SignedIn(jason);
        headers.Remove("X-CSRF-Token");

        Assert.Equal(422, (await app.SendAsync("DELETE", AvatarUrl(jasonId), headers)).Status);
        Assert.Equal(1L, app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'User' AND record_id = {jasonId}"));
    }

    string AvatarUrl(long userId) => $"/users/{TransferableUser.GenerateAvatarSignedId(app.Keys, userId)}/avatar";

    // `users(:kevin).update! avatar: fixture_file_upload(file, content_type)`
    async Task AttachAvatarAsync(long userId, string file, string contentType)
    {
        await using var source = File.OpenRead(Path.Combine(VectorFiles.Root, "reference/test/fixtures/files", file));
        using var staged = app.App.RequireStorage().Stage(source, new Filename(file), contentType);
        await app.Database.WriteAsync(tx => BlobStorage.AttachOne(tx, staged, User.ModelName, userId, AttachmentNames.Avatar, app.Now), CancellationToken.None);
    }

    // `assert_select "text"`: the SVG's <text> content.
    static string Text(string svg) => TextElement().Match(svg).Groups[1].Value.Trim();

    public void Dispose() => app.Dispose();

    [GeneratedRegex("<text[^>]*>([^<]*)</text>")]
    private static partial Regex TextElement();
}
