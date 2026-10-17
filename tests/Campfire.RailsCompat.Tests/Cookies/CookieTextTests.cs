using System.Globalization;
using System.Text;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Ruby;

namespace Campfire.RailsCompat.Tests.Cookies;

/// <summary>
/// The text a request's cookies go through: escaping, <c>Cookie</c> header parsing and <c>Set-Cookie</c> formatting.
/// <see cref="Before"/> is the code they replaced, which the Rails vectors in <see cref="CookieJarTests"/> already checked;
/// random inputs, malformed escapes and lone surrogates included, they must answer alike. They are also checked for how
/// much each call allocates, since every signed-in request runs them for a session cookie of about a kilobyte.
/// </summary>
public class CookieTextTests
{
    // Letters, the characters the escapers keep, those they don't, two escapes' worth of '%', non-ASCII, a surrogate pair and
    // lone surrogates.
    static readonly char[] Alphabet =
        "abcXYZ019 +%%=;&*-._~/\"'\\\téü日本😀𐀀? ".ToCharArray();

    static string Text(Random random, int maxLength)
    {
        var builder = new StringBuilder();
        for (var i = random.Next(maxLength + 1); i > 0; i--)
        {
            builder.Append(Alphabet[random.Next(Alphabet.Length)]);
        }
        return builder.ToString();
    }

    /// <summary>What a browser would send for some text, sometimes with a character damaged or the end cut off.</summary>
    static string Wire(Random random)
    {
        var wire = Before.PercentEncode(Text(random, 40), spaceAsPlus: true, keep: "*-._");
        switch (random.Next(4))
        {
            case 0 when wire.Length > 0:
                var at = random.Next(wire.Length);
                wire = wire[..at] + "%+"[random.Next(2)] + wire[(at + 1)..];
                break;
            case 1 when wire.Length > 0:
                wire = wire[..random.Next(wire.Length)];
                break;
            case 2:
                wire += "%4";
                break;
        }
        return wire;
    }

    [Fact]
    public void The_escapers_answer_as_before()
    {
        var random = new Random(20261014);
        for (var i = 0; i < 20_000; i++)
        {
            var text = Text(random, 60);
            Assert.Equal(Before.PercentEncode(text, spaceAsPlus: true, keep: "*-._"), RubyEscape.RackEscape(text));
            Assert.Equal(Before.PercentEncode(text, spaceAsPlus: true, keep: "_.-~"), RubyEscape.CgiEscape(text));
            Assert.Equal(Before.PercentEncode(text, spaceAsPlus: false, keep: "_.-~"), RubyEscape.UrlEncode(text));
        }
    }

    [Fact]
    public void A_long_value_is_escaped_as_before()
    {
        var text = new string('a', 300) + "日 +/=" + new string('b', 700);
        Assert.Equal(Before.PercentEncode(text, spaceAsPlus: true, keep: "*-._"), RubyEscape.RackEscape(text));
    }

    [Fact]
    public void Unescape_answers_as_before()
    {
        var random = new Random(20261015);
        for (var i = 0; i < 20_000; i++)
        {
            var wire = i % 3 == 0 ? Text(random, 40) : Wire(random);
            Assert.Equal(Before.Unescape(wire), CookieEncoding.Unescape(wire));
        }
    }

    [Fact]
    public void Cookie_headers_are_parsed_as_before()
    {
        var random = new Random(20261016);
        for (var i = 0; i < 5_000; i++)
        {
            var headers = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Header(random)).ToList();
            Assert.Equal(Before.ParseCookieHeader(headers[0]), CookieEncoding.ParseCookieHeader(headers[0]));
            Assert.Equal(Before.ParseCookieHeaders(headers), CookieEncoding.ParseCookieHeaders(headers));
        }
    }

    static string Header(Random random)
    {
        // Few names, so that some repeat; and now and then enough cookies to pass the point where duplicates are looked up in a set.
        var count = random.Next(8) == 0 ? 20 + random.Next(30) : random.Next(6);
        var parts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var name = "n" + random.Next(count > 10 ? 25 : 4);
            parts.Add(random.Next(12) switch
            {
                0 => name,
                1 => "",
                2 => " " + name + "=" + Wire(random),
                _ => name + "=" + Wire(random),
            });
        }
        return string.Join(random.Next(3) switch { 0 => ";", 1 => "; ", _ => ";  " }, parts);
    }

    [Fact]
    public void Set_cookie_headers_are_formatted_as_before()
    {
        var random = new Random(20261017);
        var now = DateTimeOffset.Parse("2026-03-02T16:00:00Z", CultureInfo.InvariantCulture);
        for (var i = 0; i < 5_000; i++)
        {
            var options = new CookieOptions
            {
                Path = random.Next(5) == 0 ? "" : "/",
                Domain = random.Next(4) == 0 ? "example.test" : null,
                Expires = random.Next(3) == 0 ? now.AddMinutes(random.Next(100_000)) : null,
                Permanent = random.Next(3) == 0,
                Secure = random.Next(2) == 0,
                HttpOnly = random.Next(2) == 0,
                SameSite = random.Next(5) switch { 0 => null, 1 => SameSiteMode.Strict, 2 => SameSiteMode.None, _ => SameSiteMode.Lax },
                Partitioned = random.Next(6) == 0,
            };
            var value = random.Next(10) == 0 ? new string('x', 1200) + Text(random, 30) : Text(random, 80);
            DateTimeOffset? at = random.Next(3) == 0 ? null : now;
            Assert.Equal(Before.FormatSetCookie("_campfire_session", value, options, at), CookieEncoding.FormatSetCookie("_campfire_session", value, options, at));
        }
    }

    // A session cookie: encrypted, so base64 and the "--" between its parts, which needs most of its characters escaped.
    static string SessionCookieValue() =>
        Convert.ToBase64String(Enumerable.Range(0, 560).Select(i => (byte)(i * 37 + 11)).ToArray()) + "--" + Convert.ToBase64String(new byte[16]);

    [Fact]
    public void A_set_cookie_header_is_formatted_in_one_allocation()
    {
        var value = SessionCookieValue();
        var options = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Permanent = true };
        var now = DateTimeOffset.Parse("2026-03-02T16:00:00Z", CultureInfo.InvariantCulture);
        var header = CookieEncoding.FormatSetCookie("_campfire_session", value, options, now);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            header = CookieEncoding.FormatSetCookie("_campfire_session", value, options, now);
        }
        var each = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // The string itself (two bytes a character and its header) and nothing else, give or take a little.
        Assert.InRange(each, 0, (header.Length * 2) + 22 + 64);
    }

    [Fact]
    public void Request_cookies_cost_only_the_strings_that_come_out()
    {
        var wire = RubyEscape.RackEscape(SessionCookieValue());
        var header = "_campfire_session=" + wire + "; session_token=" + RubyEscape.RackEscape(new string('t', 120));
        var headers = new[] { header };
        var pairs = CookieEncoding.ParseCookieHeaders(headers);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            pairs = CookieEncoding.ParseCookieHeaders(headers);
        }
        var each = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // The two values and two names as strings, the list and its array: about what the parsed pairs are worth.
        var parsed = pairs.Sum(p => (p.Key.Length + p.Value.Length) * 2L + 44);
        Assert.InRange(each, 0, parsed + 256);
    }

    /// <summary>The escaping, parsing and formatting these replaced, as they were.</summary>
    static class Before
    {
        const string hex = "0123456789ABCDEF";

        public static string PercentEncode(string s, bool spaceAsPlus, string keep)
        {
            var builder = new StringBuilder(s.Length);
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                var c = (char)b;
                if (char.IsAsciiLetterOrDigit(c) || keep.Contains(c, StringComparison.Ordinal))
                {
                    builder.Append(c);
                }
                else if (c == ' ' && spaceAsPlus)
                {
                    builder.Append('+');
                }
                else
                {
                    builder.Append('%').Append(hex[b >> 4]).Append(hex[b & 0xf]);
                }
            }
            return builder.ToString();
        }

        static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static string Unescape(string wire)
        {
            var bytes = Encoding.UTF8.GetBytes(wire);
            var outBytes = new byte[bytes.Length];
            var outLen = 0;
            var i = 0;
            while (i < bytes.Length)
            {
                var b = bytes[i];
                if (b == (byte)'+')
                {
                    outBytes[outLen++] = (byte)' ';
                    i++;
                }
                else if (b == (byte)'%')
                {
                    if (i + 2 < bytes.Length && char.IsAsciiHexDigit((char)bytes[i + 1]) && char.IsAsciiHexDigit((char)bytes[i + 2]))
                    {
                        outBytes[outLen++] = (byte)((HexVal(bytes[i + 1]) << 4) | HexVal(bytes[i + 2]));
                        i += 3;
                    }
                    else
                    {
                        return wire;
                    }
                }
                else
                {
                    outBytes[outLen++] = b;
                    i++;
                }
            }
            try
            {
                return StrictUtf8.GetString(outBytes, 0, outLen);
            }
            catch
            {
                return wire;
            }
        }

        static int HexVal(byte digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

        public static List<KeyValuePair<string, string>> ParseCookieHeader(string? header) =>
            string.IsNullOrEmpty(header) ? new List<KeyValuePair<string, string>>() : ParseCookieHeaders([header]);

        public static List<KeyValuePair<string, string>> ParseCookieHeaders(IEnumerable<string>? headers)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (headers is null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var header in headers)
            {
                var parts = header.Split(';');
                for (var i = 0; i < parts.Length; i++)
                {
                    var part = i == 0 ? parts[i] : parts[i].TrimStart(' ');
                    if (part.Length == 0) continue;

                    var eq = part.IndexOf('=');
                    var key = eq < 0 ? part : part[..eq];
                    var value = eq < 0 ? "" : part[(eq + 1)..];

                    if (seen.Add(key))
                    {
                        result.Add(new KeyValuePair<string, string>(key, Unescape(value)));
                    }
                }
            }
            return result;
        }

        public static string FormatSetCookie(string name, string rawValue, CookieOptions options, DateTimeOffset? now = null)
        {
            var builder = new StringBuilder();
            builder.Append(name).Append('=').Append(PercentEncode(rawValue, spaceAsPlus: true, keep: "*-._"));

            if (!string.IsNullOrEmpty(options.Domain))
            {
                builder.Append("; domain=").Append(options.Domain);
            }

            if (!string.IsNullOrEmpty(options.Path))
            {
                builder.Append("; path=").Append(options.Path);
            }

            var expires = options.Expires;
            if (options.Permanent && expires is null && now.HasValue)
            {
                expires = now.Value.AddYears(20);
            }

            if (expires.HasValue)
            {
                builder.Append("; expires=").Append(expires.Value.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture));
            }

            if (options.Secure)
            {
                builder.Append("; secure");
            }

            if (options.HttpOnly)
            {
                builder.Append("; httponly");
            }

            if (options.SameSite.HasValue)
            {
                builder.Append("; samesite=").Append(options.SameSite.Value switch
                {
                    SameSiteMode.Lax => "lax",
                    SameSiteMode.Strict => "strict",
                    SameSiteMode.None => "none",
                    _ => "lax"
                });
            }

            if (options.Partitioned)
            {
                builder.Append("; partitioned");
            }

            return builder.ToString();
        }
    }
}
