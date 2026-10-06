using System.Buffers.Binary;
using System.Text;
using Campfire.Storage.Media;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.Accounts;

/// <summary>
/// reference/test/controllers/{users,accounts}_controller_test.rb and
/// accounts/{logos,custom_styles,join_codes}_controller_test.rb, on the parity seed with CSRF
/// protection on (the token sent as Turbo sends it). David and Jason administer; Kevin and JZ are
/// members.
/// </summary>
public sealed class AccountsReferenceTests : IDisposable
{
    const string David = "DavidSessionToken0000001";
    const string Kevin = "KevinSessionToken0000002";
    const string JoinCode = "CRMu-l8Ge-KB9B";

    readonly MessagesApp app = new(
        new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
        [
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900001, 127326141, '{David}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
            "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " +
                $"VALUES (900002, 712064548, '{Kevin}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
        ]);

    Dictionary<string, string> SignedOut()
    {
        var headers = app.SignedIn("none");
        headers["Cookie"] = string.Join("; ", headers["Cookie"].Split("; ").Where(cookie => !cookie.StartsWith("session_token=", StringComparison.Ordinal)));
        return headers;
    }

    [Fact]
    public async Task Show()
    {
        Assert.Equal(200, (await app.SendAsync("GET", "/users/127326141", app.SignedIn(David, "text/html"))).Status);
    }

    [Fact]
    public async Task New()
    {
        Assert.Equal(200, (await app.SendAsync("GET", $"/join/{JoinCode}", SignedOut())).Status);
    }

    [Fact]
    public async Task New_does_not_allow_a_signed_in_user()
    {
        var response = await app.SendAsync("GET", $"/join/{JoinCode}", app.SignedIn(David));
        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task New_requires_a_join_code()
    {
        Assert.Equal(404, (await app.SendAsync("GET", "/join/not", SignedOut())).Status);
    }

    [Fact]
    public async Task Create()
    {
        var users = Count("users");
        var response = await app.SendAsync("POST", $"/join/{JoinCode}", SignedOut(),
            "user%5Bname%5D=New+Person&user%5Bemail_address%5D=new%4037signals.com&user%5Bpassword%5D=secret123456");

        Assert.Equal(302, response.Status);
        Assert.Equal("http://campfire.test/", response.Headers.Location.ToString());
        Assert.Equal(users + 1, Count("users"));
        var user = (long)app.Scalar("SELECT id FROM users ORDER BY id DESC LIMIT 1")!;
        Assert.Contains(response.Headers.SetCookie, cookie => cookie!.StartsWith("session_token=", StringComparison.Ordinal));
        Assert.Equal(1L, app.Scalar($"SELECT COUNT(*) FROM sessions WHERE user_id = {user}"));
        // `assert_equal Rooms::Open.all, user.rooms`
        Assert.Equal(
            app.Scalar("SELECT group_concat(id) FROM (SELECT id FROM rooms WHERE type = 'Rooms::Open' ORDER BY id)"),
            app.Scalar($"SELECT group_concat(room_id) FROM (SELECT room_id FROM memberships WHERE user_id = {user} ORDER BY room_id)"));
    }

    [Fact]
    public async Task Creating_a_new_user_with_an_existing_email_address_will_redirect_to_login_screen()
    {
        var users = Count("users");
        var response = await app.SendAsync("POST", $"/join/{JoinCode}", SignedOut(),
            "user%5Bname%5D=Another+David&user%5Bemail_address%5D=david%4037signals.com&user%5Bpassword%5D=secret123456");

        Assert.Equal(users, Count("users"));
        Assert.Equal("http://campfire.test/session/new?email_address=david%4037signals.com", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Edit()
    {
        Assert.Equal(200, (await app.SendAsync("GET", "/account/edit", app.SignedIn(David, "text/html"))).Status);
    }

    [Fact]
    public async Task Edit_groups_administrators_separately_from_members_with_a_divider()
    {
        var response = await app.SendAsync("GET", "/account/edit", app.SignedIn(David, "text/html"));

        Assert.Equal(200, response.Status);
        var frame = response.Body[response.Body.IndexOf("<turbo-frame id=\"account_users\">", StringComparison.Ordinal)..];
        var divider = frame.IndexOf("hr class=\"separator full-width\"", StringComparison.Ordinal);
        Assert.True(divider > 0, "Divider should exist in the response");
        foreach (var administrator in new[] { "David", "Jason" })
        {
            var at = frame.IndexOf($"<strong>{administrator}</strong>", StringComparison.Ordinal);
            Assert.InRange(at, 0, divider);
        }
        foreach (var member in new[] { "JZ", "Kevin" })
        {
            Assert.True(frame.IndexOf($"<strong>{member}</strong>", StringComparison.Ordinal) > divider, $"Member {member} should appear after the divider");
        }
    }

    [Fact]
    public async Task Update()
    {
        var response = await app.SendAsync("PUT", "/account", app.SignedIn(David), "account%5Bname%5D=Different");

        Assert.Equal("http://campfire.test/account/edit", response.Headers.Location.ToString());
        Assert.Equal("Different", app.Scalar("SELECT name FROM accounts"));
    }

    [Fact]
    public async Task Non_admins_cannot_update()
    {
        var response = await app.SendAsync("PUT", "/account", app.SignedIn(Kevin), "account%5Bname%5D=Different");

        Assert.Equal(403, response.Status);
        Assert.Equal("37signals", app.Scalar("SELECT name FROM accounts"));
    }

    [Fact]
    public async Task Update_without_a_csrf_token_is_rejected()
    {
        var headers = app.SignedIn(David);
        headers.Remove("X-CSRF-Token");
        Assert.Equal(422, (await app.SendAsync("PUT", "/account", headers, "account%5Bname%5D=Different")).Status);
        Assert.Equal("37signals", app.Scalar("SELECT name FROM accounts"));
    }

    [Fact]
    public async Task Show_stock()
    {
        var response = await GetLogoAsync("/account/logo");
        AssertValidPng(response, 512);
    }

    [Fact]
    public async Task Show_stock_small_size()
    {
        var response = await GetLogoAsync("/account/logo?size=small");
        AssertValidPng(response, 192);
    }

    [Fact]
    public async Task Show_custom()
    {
        Assert.SkipUnless(LibVips.IsAvailable, "libvips isn't installed");
        await UploadLogo("moon.jpg", "image/jpeg");

        AssertValidPng(await GetLogoAsync("/account/logo"), 512);
        AssertValidPng(await GetLogoAsync("/account/logo?size=small"), 192);
    }

    [Fact]
    public async Task Show_stock_when_custom_logo_cannot_be_resized()
    {
        await UploadLogo("pixel.bmp", "image/bmp");

        var response = await GetLogoAsync("/account/logo");
        AssertValidPng(response, 512);
        Assert.Contains("app-icon.png", response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Destroy()
    {
        await UploadLogo("moon.jpg", "image/jpeg");

        var response = await app.SendAsync("DELETE", "/account/logo", app.SignedIn(David));

        Assert.Equal("http://campfire.test/account/edit", response.Headers.Location.ToString());
        Assert.Equal(0L, app.Scalar("SELECT COUNT(*) FROM active_storage_attachments WHERE record_type = 'Account'"));
    }

    [Fact]
    public async Task Custom_styles_edit()
    {
        Assert.Equal(200, (await app.SendAsync("GET", "/account/custom_styles/edit", app.SignedIn(David, "text/html"))).Status);
    }

    [Fact]
    public async Task Custom_styles_update()
    {
        var response = await app.SendAsync("PUT", "/account/custom_styles", app.SignedIn(David),
            "account%5Bcustom_styles%5D=%3Aroot+%7B+--color-text%3A+red%3B+%7D");

        Assert.Equal("http://campfire.test/account/custom_styles/edit", response.Headers.Location.ToString());
        Assert.Equal(":root { --color-text: red; }", app.Scalar("SELECT custom_styles FROM accounts"));
    }

    [Fact]
    public async Task Non_admins_cannot_update_custom_styles()
    {
        var response = await app.SendAsync("PUT", "/account/custom_styles", app.SignedIn(Kevin),
            "account%5Bcustom_styles%5D=%3Aroot+%7B+--color-text%3A+red%3B+%7D");
        Assert.Equal(403, response.Status);
    }

    [Fact]
    public async Task Custom_styles_are_not_escaped_into_markup_beyond_the_style_tag()
    {
        // An independent check: the stored CSS is written raw only inside <style>, as Rails does.
        await app.SendAsync("PUT", "/account/custom_styles", app.SignedIn(David), "account%5Bcustom_styles%5D=body+%7B+color%3A+red+%7D");
        var page = await app.SendAsync("GET", "/account/custom_styles/edit", app.SignedIn(David, "text/html"));
        Assert.Contains("<style data-turbo-track=\"reload\">body { color: red }</style>", page.Body, StringComparison.Ordinal);
        Assert.Contains(">\nbody { color: red }</textarea>", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_new_join_code()
    {
        var response = await app.SendAsync("POST", "/account/join_code", app.SignedIn(David));

        Assert.Equal("http://campfire.test/account/edit", response.Headers.Location.ToString());
        Assert.NotEqual(JoinCode, app.Scalar("SELECT join_code FROM accounts"));
        Assert.Matches("^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$", (string)app.Scalar("SELECT join_code FROM accounts")!);
    }

    [Fact]
    public async Task Only_administrators_can_create_new_join_codes()
    {
        Assert.Equal(403, (await app.SendAsync("POST", "/account/join_code", app.SignedIn(Kevin))).Status);
        Assert.Equal(JoinCode, app.Scalar("SELECT join_code FROM accounts"));
    }

    async Task UploadLogo(string filename, string contentType)
    {
        const string boundary = "----a03LogoBoundary";
        var head = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"account[logo]\"; filename=\"{filename}\"\r\nContent-Type: {contentType}\r\n\r\n");
        var file = File.ReadAllBytes(Path.Combine(Vectors.VectorFiles.Root, "reference/test/fixtures/files", filename));
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var (status, _, _) = await SendBytesAsync("PATCH", "/account", app.SignedIn(David), [.. head, .. file, .. tail], $"multipart/form-data; boundary={boundary}");
        Assert.Equal(302, status);
    }

    long Count(string table) => (long)app.Scalar($"SELECT COUNT(*) FROM {table}")!;

    // A request with a binary body, answered with the response's bytes.
    async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> SendBytesAsync(
        string method, string target, IReadOnlyDictionary<string, string> headers, byte[]? body = null, string? contentType = null)
    {
        var context = new DefaultHttpContext();
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Method = method;
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.7");
        context.Request.Headers.Host = MessagesApp.Host;
        foreach (var (header, value) in headers)
        {
            context.Request.Headers[header] = value;
        }
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body ?? []);
        context.Request.ContentLength = body?.Length ?? 0;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await app.App.HandleAsync(context);
        return (context.Response.StatusCode, context.Response.Headers, responseBody.ToArray());
    }

    async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> GetLogoAsync(string target) =>
        await SendBytesAsync("GET", target, app.SignedIn(David));

    // `assert_valid_png_response size:`: a PNG whose IHDR says it's size × size.
    static void AssertValidPng((int Status, IHeaderDictionary Headers, byte[] Body) response, int size)
    {
        Assert.Equal("image/png", response.Headers.ContentType.ToString());
        var bytes = response.Body;
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    public void Dispose() => app.Dispose();
}
