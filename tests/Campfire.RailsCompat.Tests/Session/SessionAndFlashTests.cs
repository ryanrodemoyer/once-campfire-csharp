using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Session;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Session;

public class SessionAndFlashTests
{
    static readonly string SecretKeyBase = RailsCompatVectors.File.SecretKeyBase;
    static readonly DateTimeOffset FrozenNow = DateTimeOffset.Parse(RailsCompatVectors.File.Now, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    static CookieJar CreateJar(string? header = null) =>
        CookieJar.FromHeader(header, SecretKeyBase, () => FrozenNow);

    [Fact]
    public void Flash_round_trip_discards_after_one_request()
    {
        var flash = new Flash();
        flash.Notice = "✓";

        var stored = flash.ToSessionValue();
        Assert.NotNull(stored);
        Assert.Equal("[]", stored["discard"]?.ToString());
        Assert.Equal("✓", stored["flashes"]?["notice"]?.ToString());

        // Next request loads flash
        var next = Flash.FromSessionValue(stored);
        Assert.Equal("✓", next.Notice);

        // At end of this request, notice was discarded
        Assert.Null(next.ToSessionValue());
    }

    [Fact]
    public void Flash_now_is_not_persisted()
    {
        var flash = new Flash();
        flash.Now.Alert = "Too many requests or unauthorized.";

        Assert.Equal("Too many requests or unauthorized.", flash.Alert);
        Assert.Null(flash.ToSessionValue());
    }

    [Fact]
    public void Flash_keep_and_legacy_discard_lists()
    {
        var stored = new JsonObject
        {
            ["discard"] = new JsonArray("alert"),
            ["flashes"] = new JsonObject
            {
                ["notice"] = "hi",
                ["alert"] = "gone"
            }
        };

        var flash = Flash.FromSessionValue(stored);
        Assert.Null(flash.Alert);
        Assert.Equal("hi", flash.Notice);

        flash.Keep("notice");
        var nextStored = flash.ToSessionValue();
        Assert.NotNull(nextStored);
        Assert.Equal("hi", nextStored["flashes"]?["notice"]?.ToString());
    }

    [Fact]
    public void Sids_are_32_hex_chars()
    {
        var sid = Campfire.RailsCompat.Session.Session.GenerateSessionId();
        Assert.Equal(32, sid.Length);
        Assert.All(sid, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void Decrypts_reference_campfire_session_cookie()
    {
        var sessionVector = RailsCompatVectors.File.Session;
        var jar = CreateJar(sessionVector.SetCookie);
        var session = new Campfire.RailsCompat.Session.Session(jar);
        session.Load();

        Assert.Equal(sessionVector.Session["session_id"], session.Id);
        Assert.Equal(sessionVector.Session["_csrf_token"], session.CsrfToken);
    }

    [Fact]
    public void Flash_survives_exactly_one_redirect_matching_rails()
    {
        // Request 1: Controller sets return_to and flash notice, then redirects
        var jar1 = CreateJar();
        var session1 = new Campfire.RailsCompat.Session.Session(jar1);
        session1.ReturnToAfterAuthenticating = "/rooms/1";
        session1.Flash.Notice = "Signed in successfully";
        session1.Commit();

        var setCookies1 = jar1.ToSetCookieHeaders();
        Assert.Single(setCookies1);
        Assert.StartsWith("_campfire_session=", setCookies1[0]);

        // Request 2 (the redirect destination): Reads session and flash
        var jar2 = CreateJar(setCookies1[0]);
        var session2 = new Campfire.RailsCompat.Session.Session(jar2);
        session2.Load();

        Assert.Equal("/rooms/1", session2.ReturnToAfterAuthenticating);
        Assert.Equal("Signed in successfully", session2.Flash.Notice);

        // User finishes authenticating: remove return_to, and commit session
        session2.Remove("return_to_after_authenticating");
        session2.Commit();

        var setCookies2 = jar2.ToSetCookieHeaders();
        Assert.Single(setCookies2);

        // Request 3 (subsequent navigation): Flash is gone!
        var jar3 = CreateJar(setCookies2[0]);
        var session3 = new Campfire.RailsCompat.Session.Session(jar3);
        session3.Load();

        Assert.Null(session3.Flash.Notice);
        Assert.Null(session3.ReturnToAfterAuthenticating);
    }

    [Fact]
    public void Reset_session_drops_data_and_allocates_new_id()
    {
        var jar = CreateJar();
        var session = new Campfire.RailsCompat.Session.Session(jar);
        session.CsrfToken = "token1";
        var oldId = session.Id;

        session.Reset();
        Assert.NotEqual(oldId, session.Id);
        Assert.Null(session.CsrfToken);
        Assert.True(session.IsChanged);
    }

    [Fact]
    public void Session_commit_deletes_cookie_when_only_session_id_remains()
    {
        // When session has data, cookie is set
        var jar = CreateJar();
        var session = new Campfire.RailsCompat.Session.Session(jar);
        session.CsrfToken = "some_csrf";
        session.Commit();

        var headers = jar.ToSetCookieHeaders();
        Assert.Single(headers);
        Assert.StartsWith("_campfire_session=", headers[0]);

        // When all extra data is removed, commit deletes the session cookie
        var jar2 = CreateJar(headers[0]);
        var session2 = new Campfire.RailsCompat.Session.Session(jar2);
        session2.Remove("_csrf_token");
        session2.Commit();

        var headers2 = jar2.ToSetCookieHeaders();
        Assert.Single(headers2);
        Assert.StartsWith("_campfire_session=; path=/; max-age=0", headers2[0]);
    }
}
