using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Cookies;

public class CookieJarTests
{
    static readonly string SecretKeyBase = RailsCompatVectors.File.SecretKeyBase;
    static readonly DateTimeOffset FrozenNow = DateTimeOffset.Parse(RailsCompatVectors.File.Now, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    static CookieJar CreateJar(string? header = null, DateTimeOffset? now = null) =>
        CookieJar.FromHeader(header, SecretKeyBase, () => now ?? FrozenNow);

    [Theory]
    [MemberData(nameof(RailsCompatVectors.CookieEscaping), MemberType = typeof(RailsCompatVectors))]
    public void Cookie_escaping_vectors_match_rails(CookieEscapingCase c)
    {
        if (c.Raw is not null)
        {
            Assert.Equal(c.Wire, CookieEncoding.Escape(c.Raw));
        }
        Assert.Equal(c.Parsed, CookieEncoding.Unescape(c.Wire));
    }

    [Fact]
    public void Parses_request_cookies()
    {
        var jar = CreateJar("a=1; b=x%20y+z;c=3; a=2; d");
        Assert.Equal("1", jar["a"]);
        Assert.Equal("x y z", jar["b"]);
        Assert.Equal("3", jar["c"]);
        Assert.Equal("", jar["d"]);
        Assert.Null(jar["zz"]);
    }

    [Fact]
    public void Sets_plain_cookies_with_rails_defaults()
    {
        var jar = CreateJar();
        jar.Set("last_room", "42");
        var headers = jar.ToSetCookieHeaders(ssl: false, host: "example.com");
        Assert.Equal(["last_room=42; path=/; samesite=lax"], headers);
    }

    [Fact]
    public void Unchanged_values_are_not_rewritten_unless_expiring()
    {
        var jar = CreateJar("last_room=42");
        jar.Set("last_room", "42");
        Assert.Empty(jar.ToSetCookieHeaders(ssl: false, host: "h"));

        jar.Permanent.Set("last_room", "42");
        var headers = jar.ToSetCookieHeaders(ssl: false, host: "h");
        var expectedExpiry = FrozenNow.AddYears(20).ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
        Assert.Equal([$"last_room=42; path=/; expires={expectedExpiry}; samesite=lax"], headers);
    }

    [Fact]
    public void Escapes_values_on_the_wire()
    {
        var jar = CreateJar();
        jar.Set("x", "a b+c/=");
        var headers = jar.ToSetCookieHeaders(ssl: false, host: "h");
        Assert.Equal(["x=a+b%2Bc%2F%3D; path=/; samesite=lax"], headers);
    }

    [Fact]
    public void Signed_permanent_httponly_round_trip()
    {
        var jar = CreateJar();
        jar.Signed.Permanent.Set("session_token", "tok", new CookieOptions { HttpOnly = true });
        var headers = jar.ToSetCookieHeaders(ssl: true, host: "h");
        Assert.Single(headers);
        Assert.StartsWith("session_token=", headers[0]);
        Assert.EndsWith("; httponly; samesite=lax", headers[0]);
        Assert.Equal("tok", jar.Signed["session_token"]);

        var raw = jar["session_token"]!;
        var nextJar = CreateJar($"session_token={CookieEncoding.Escape(raw)}");
        Assert.Equal("tok", nextJar.Signed["session_token"]);
    }

    [Fact]
    public void Tampered_signed_cookies_read_as_nil()
    {
        var jar = CreateJar("session_token=forged");
        Assert.Null(jar.Signed["session_token"]);
        Assert.Equal("forged", jar["session_token"]);
    }

    [Fact]
    public void Encrypted_round_trip()
    {
        var jar = CreateJar();
        var node = new JsonObject { ["a"] = 1 };
        jar.Encrypted.Set("secret", node);
        var readNode = jar.Encrypted["secret"];
        Assert.NotNull(readNode);
        Assert.True(JsonNode.DeepEquals(node, readNode));

        var headers = jar.ToSetCookieHeaders();
        Assert.Single(headers);
        var nextJar = CreateJar(headers[0]);
        var nextNode = nextJar.Encrypted["secret"];
        Assert.NotNull(nextNode);
        Assert.True(JsonNode.DeepEquals(node, nextNode));
    }

    [Fact]
    public void Overflow_throws_cookie_overflow_exception()
    {
        var jar = CreateJar();
        var big = new string('x', CookieEncoding.MaxCookieSize);
        Assert.Throws<CookieOverflowException>(() => jar.Signed.Set("big", big));
        Assert.Throws<CookieOverflowException>(() => jar.Encrypted.Set("big", JsonValue.Create(big)));
    }

    [Fact]
    public void Delete_only_emits_header_when_present()
    {
        var jar = CreateJar("session_token=abc");
        jar.Delete("missing");
        jar.Delete("session_token");

        Assert.True(jar.IsDeleted("session_token"));
        Assert.Null(jar["session_token"]);

        var headers = jar.ToSetCookieHeaders(ssl: false, host: "h");
        Assert.Equal(["session_token=; path=/; max-age=0; expires=Thu, 01 Jan 1970 00:00:00 GMT; samesite=lax"], headers);
    }

    [Fact]
    public void Setting_and_then_deleting_a_cookie_emits_both_headers_in_order()
    {
        var jar = CreateJar();
        jar.Set("session_token", "abc");
        jar.Delete("session_token");

        var headers = jar.ToSetCookieHeaders(ssl: false, host: "h");
        Assert.Equal(2, headers.Count);
        Assert.StartsWith("session_token=abc", headers[0]);
        Assert.StartsWith("session_token=;", headers[1]);
    }

    [Fact]
    public void Secure_cookies_require_ssl_or_onion()
    {
        var jar = CreateJar();
        jar.Set("s", "1", new CookieOptions { Secure = true });

        Assert.Empty(jar.ToSetCookieHeaders(ssl: false, host: "example.com"));
        Assert.Single(jar.ToSetCookieHeaders(ssl: false, host: "x.onion"));
        Assert.Equal(["s=1; path=/; secure; samesite=lax"], jar.ToSetCookieHeaders(ssl: true, host: "example.com"));
    }

    [Theory]
    [MemberData(nameof(CampfireVectors.Sessions), MemberType = typeof(CampfireVectors))]
    public void Sessions_from_reference_authenticate_on_csharp(SessionCookieCase c)
    {
        var jar = CookieJar.FromHeader(c.CookieHeader, SecretKeyBase, () => DateTimeOffset.Parse("2024-09-26T13:14:52.877Z", CultureInfo.InvariantCulture));
        Assert.Equal(c.Token, jar.Signed["session_token"]);
    }

    [Fact]
    public void Forged_token_vector_authenticates_to_nonexistent_token()
    {
        var forged = CampfireVectors.SessionsFile.Forged;
        var jar = CreateJar(forged.CookieHeader);
        Assert.Equal("not-a-session-token", jar.Signed["session_token"]);
    }

    [Fact]
    public void Tampered_or_wrong_secret_session_cookie_is_rejected()
    {
        var forged = CampfireVectors.SessionsFile.Forged;
        var wrongSecretJar = CookieJar.FromHeader(forged.CookieHeader, "wrong-secret-key-base-that-does-not-match-reference", () => FrozenNow);
        Assert.Null(wrongSecretJar.Signed["session_token"]);

        var tamperedJar = CreateJar("session_token=tampered--0000");
        Assert.Null(tamperedJar.Signed["session_token"]);
    }

    [Fact]
    public void Session_token_issued_by_csharp_matches_reference()
    {
        var session = RailsCompatVectors.File.Session;
        var jar = CreateJar();
        jar.SetAuthenticationCookie(session.SessionTokenValue);

        var headers = jar.ToSetCookieHeaders();
        Assert.Single(headers);
        Assert.Equal(session.SessionTokenSetCookie, headers[0]);
        Assert.Equal(session.SessionTokenRaw, jar["session_token"]);
    }

    [Fact]
    public void Reference_session_token_authenticates_and_matches_value()
    {
        var session = RailsCompatVectors.File.Session;
        var jar = CreateJar(session.SessionTokenSetCookie);
        Assert.Equal(session.SessionTokenValue, jar.Signed["session_token"]);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedCookieGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Signed_cookie_generation_matches_vector(SignedCookieGenerate c)
    {
        var jar = CreateJar(now: c.ExpiresAt is null ? null : DateTimeOffset.Parse(c.ExpiresAt, CultureInfo.InvariantCulture));
        jar.Signed.Set(c.Name, c.Value, new CookieOptions
        {
            Expires = c.ExpiresAt is null ? null : DateTimeOffset.Parse(c.ExpiresAt, CultureInfo.InvariantCulture)
        });

        Assert.Equal(c.Raw, jar[c.Name]);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedCookieVerify), MemberType = typeof(RailsCompatVectors))]
    public void Signed_cookie_verification_matches_vector(SignedCookieVerify c)
    {
        var jar = CreateJar($"{c.Name}={CookieEncoding.Escape(c.Raw)}", DateTimeOffset.Parse(c.Now, CultureInfo.InvariantCulture));
        var node = jar.Signed.GetJson(c.Name);
        var expectedJson = c.Expected.GetRawText();
        var actualJson = node is null ? "null" : RailsJson.Generate(node);

        if (c.Expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Equal("null", actualJson);
        }
        else
        {
            Assert.True(RailsJson.TryParse(expectedJson, out var expectedNode));
            Assert.Equal(RailsJson.Generate(expectedNode), actualJson);
        }
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.EncryptedCookieVerify), MemberType = typeof(RailsCompatVectors))]
    public void Encrypted_cookie_verification_matches_vector(EncryptedCookieVerify c)
    {
        var jar = CreateJar($"{c.Name}={CookieEncoding.Escape(c.Raw)}", DateTimeOffset.Parse(c.Now, CultureInfo.InvariantCulture));
        var node = jar.Encrypted.Get(c.Name);
        var expectedJson = c.Expected.GetRawText();
        var actualJson = node is null ? "null" : RailsJson.Generate(node);

        if (c.Expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Equal("null", actualJson);
        }
        else
        {
            Assert.True(RailsJson.TryParse(expectedJson, out var expectedNode));
            Assert.Equal(RailsJson.Generate(expectedNode), actualJson);
        }
    }
}
