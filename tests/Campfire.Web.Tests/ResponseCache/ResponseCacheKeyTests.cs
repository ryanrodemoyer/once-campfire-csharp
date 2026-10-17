using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Ruby;
using Campfire.Web.Pipeline;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary>
/// <c>response_cache_key</c> (reference/app/controllers/concerns/cached_responses.rb): the JSON array a cacheable request is
/// stored under. <see cref="Before"/> is the code the key was built with, which built a string and a JSON node for each
/// piece; the key now written into one buffer must be the same text, since its length decides what is too long to cache
/// and an unequal key is a miss.
/// </summary>
public class ResponseCacheKeyTests
{
    // Plain text, what the escaper changes (quotes, backslashes, controls, the HTML three), non-ASCII, U+2028 and a lone surrogate.
    static readonly char[] Alphabet =
        "abcXYZ019 /?=&<>\"'\\\t\n\r\b\f\u0001\u001f\u007f:;,.-_~%+\u00e9\u65e5\u2028\u2029\ud83d\ude00\ud800".ToCharArray();

    static string Text(Random random, int maxLength)
    {
        var builder = new StringBuilder();
        for (var i = random.Next(maxLength + 1); i > 0; i--)
        {
            builder.Append(Alphabet[random.Next(Alphabet.Length)]);
        }
        return builder.ToString();
    }

    // Mostly plain text, as a path or a user agent is.
    static string Plain(Random random, int length)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            builder.Append((char)('a' + random.Next(26)));
        }
        return builder.ToString();
    }

    [Fact]
    public void A_string_is_written_as_it_was()
    {
        var random = new Random(20261020);
        Assert.Equal("null", ApplicationController.Json(null));
        Assert.Equal("\"\"", ApplicationController.Json(""));
        for (var i = 0; i < 20_000; i++)
        {
            var text = i % 4 == 0 ? Plain(random, random.Next(60)) : Text(random, 50);
            Assert.Equal(Before.Json(text), ApplicationController.Json(text));
        }
    }

    [Fact]
    public void The_html_characters_and_controls_are_still_escaped()
    {
        Assert.Equal("\"a\\u003cb\\u003e\\u0026\\\"\\\\\\n\\u001f\"", ApplicationController.Json("a<b>&\"\\\n\u001f"));
        // U+2028 stays raw, as in Rails 8.2.
        Assert.Equal("\"\u2028\"", ApplicationController.Json("\u2028"));
    }

    [Fact]
    public void The_key_is_the_same_text_as_before()
    {
        var random = new Random(20261021);
        for (var i = 0; i < 5_000; i++)
        {
            var cookies = Jar(random);
            var encoding = random.Next(3) switch { 0 => "gzip", 1 => "identity", _ => "br" };
            var path = i % 3 == 0 ? Text(random, 40) : "/rooms/" + random.Next(1000) + "?" + Plain(random, random.Next(30));
            var agent = random.Next(8) == 0 ? null : (i % 2 == 0 ? Text(random, 60) : Plain(random, 120));
            var accept = random.Next(4) == 0 ? null : "text/html, application/xhtml+xml";
            var turbo = random.Next(3) == 0 ? "sidebar" : null;
            var token = Plain(random, 24);
            var userId = random.NextInt64(1, 2_000_000_000);
            var hash = "\"" + Plain(random, 16) + "\"";

            var expected = Before.Key("rooms", path, "http://example.test", agent, encoding, accept, turbo, userId, token, hash, cookies);
            var key = ApplicationController.BuildResponseCacheKey("rooms", path, "http://example.test", agent, encoding, accept, turbo, userId, token, hash, cookies);
            Assert.Equal(expected, key);
        }
    }

    // A jar with some of the session's two cookies, which the key leaves out, and a few others in an order of their own.
    static CookieJar Jar(Random random)
    {
        var names = new[] { "last_room", "_campfire_session", "session_token", "theme", "a", "Z", "b-c", "z_1" };
        var parts = new List<string>();
        foreach (var name in names.OrderBy(_ => random.Next()).Take(random.Next(names.Length + 1)))
        {
            parts.Add(name + "=" + RubyEscape.RackEscape(Text(random, 20)));
        }
        return CookieJar.FromHeader(string.Join("; ", parts), "secret-key-base");
    }

    [Fact]
    public void The_key_is_built_in_one_allocation_for_a_typical_request()
    {
        var cookies = CookieJar.FromHeader("last_room=486777696; _campfire_session=abc; session_token=def", "secret-key-base");
        string Build() => ApplicationController.BuildResponseCacheKey(
            "rooms", "/rooms/486777696", "https://campfire.example.test", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/126.0",
            "gzip", "text/html, application/xhtml+xml", null, 1_234_567, "gk3Zq1pQe7oT4mLw9sVxYb2d", "\"0123456789abcdef0123456789abcdef\"", cookies);

        var key = Build();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            key = Build();
        }
        var each = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // The string and nothing else (the cookie jar's enumerator and a few bytes of slack).
        Assert.InRange(each, 0, (key.Length * 2) + 22 + 128);
    }

    /// <summary>The key as it was built.</summary>
    static class Before
    {
        public static string Json(string? value) => value is null ? "null" : RailsJson.Encode(JsonValue.Create(value));

        public static string Key(
            string controllerPath, string fullPath, string baseUrl, string? userAgent, string encoding, string? accept, string? turboFrame,
            long userId, string sessionToken, string sessionHash, CookieJar cookies) =>
            string.Concat(
                "[",
                Json(controllerPath), ",",
                Json(fullPath), ",",
                Json(baseUrl), ",",
                Json(userAgent), ",",
                Json(encoding), ",",
                Json(accept), ",",
                Json(turboFrame), ",",
                Json("en"), ",",
                userId.ToString(CultureInfo.InvariantCulture), ",",
                Json(sessionToken), ",",
                sessionHash, ",",
                CookieKey(cookies),
                "]");

        static string CookieKey(CookieJar cookies)
        {
            var builder = new StringBuilder("{");
            var first = true;
            foreach (var (name, value) in cookies.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (name is "_campfire_session" or "session_token")
                {
                    continue;
                }
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                builder.Append(Json(name)).Append(':').Append(Json(value));
            }
            builder.Append('}');
            return builder.ToString();
        }
    }
}
