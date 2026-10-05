using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Tests.Crypto;

public class RubyBase64Tests
{
    [Fact]
    public void Url_safe_decode_is_lenient_like_ruby()
    {
        Assert.Equal("A"u8.ToArray(), RubyBase64.UrlSafeDecode("QQ"));
        Assert.Equal("A"u8.ToArray(), RubyBase64.UrlSafeDecode("QQ=="));
        Assert.Null(RubyBase64.UrlSafeDecode("QQ="));
        Assert.Null(RubyBase64.UrlSafeDecode("QR"));
        Assert.Equal(RubyBase64.UrlSafeDecode("a+b/"), RubyBase64.UrlSafeDecode("a-b_"));
    }

    [Theory]
    [InlineData("QQ")]
    [InlineData("QR==")]
    [InlineData("QUI=x")]
    [InlineData("QU J")]
    [InlineData("Q\nQ=")]
    [InlineData("a-b_")]
    [InlineData("====")]
    public void Strict_decode_refuses_what_ruby_refuses(string encoded) => Assert.Null(RubyBase64.StrictDecode(encoded));

    [Fact]
    public void Encodes_each_flavor()
    {
        byte[] data = [0xfb, 0xff];
        Assert.Equal("+/8=", RubyBase64.StrictEncode(data));
        Assert.Equal("-_8", RubyBase64.UrlSafeEncode(data, padding: false));
        Assert.Equal("-_8=", RubyBase64.UrlSafeEncode(data, padding: true));
        Assert.Equal(data, RubyBase64.StrictDecode("+/8="));
    }
}

public class RubyMarshalTests
{
    [Fact]
    public void Loads_strings()
    {
        // Marshal.dump("gid://campfire/User/1")
        Assert.Equal("gid://campfire/User/1"u8.ToArray(), RubyMarshal.LoadString("\u0004\u0008I\"\u001agid://campfire/User/1\u0006:\u0006ET"u8));
        // A 300-byte string has a two-byte length.
        byte[] longString = [0x04, 0x08, (byte)'I', (byte)'"', 0x02, 0x2c, 0x01, .. Enumerable.Repeat((byte)'x', 300)];
        Assert.Equal(300, RubyMarshal.LoadString(longString)!.Length);
        Assert.Null(RubyMarshal.LoadString("\u0004\u0008i\u0006"u8));
        Assert.Equal("short"u8.ToArray(), RubyMarshal.LoadString("\u0004\u0008\"\u000ashort"u8));
        Assert.Null(RubyMarshal.LoadString("\u0004\u0008\"\u000bshort"u8));
    }
}

public class RailsJsonTests
{
    [Fact]
    public void Escapes_html_entities_but_not_separators_or_slashes()
    {
        Assert.Equal(
            "{\"key\":\"\\u003ca href=\\\"/x\\\"\\u003e\\u0026\\u003c/a\\u003e\u2028\"}",
            RailsJson.Encode(new JsonObject { ["key"] = "<a href=\"/x\">&</a>\u2028" }));
        Assert.Equal("\"<&>\"", RailsJson.Generate(JsonValue.Create("<&>")));
    }

    [Fact]
    public void Escapes_control_characters_like_the_json_gem() =>
        Assert.Equal("\"\\u001f\\n\\t\\b\\f\u007fé\"", RailsJson.Encode(JsonValue.Create("\u001f\n\t\b\f\u007fé")));

    // JSON.generate(f) in the reference (json 2.21.2).
    [Theory]
    [InlineData(320.0, "320.0")]
    [InlineData(65.84, "65.84")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "0.00001")]
    [InlineData(1.25e-5, "0.0000125")]
    [InlineData(1.5e-7, "0.00000015")]
    [InlineData(-1.5e-7, "-0.00000015")]
    [InlineData(1e-7, "0.0000001")]
    [InlineData(1.2e-9, "0.0000000012")]
    [InlineData(1.23456789012e-8, "0.0000000123456789012")]
    [InlineData(1e-10, "1e-10")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1e14, "100000000000000.0")]
    [InlineData(-1e14, "-100000000000000.0")]
    [InlineData(123456789012345.6, "123456789012345.6")]
    [InlineData(1e15, "1e+15")]
    [InlineData(-1e15, "-1e+15")]
    [InlineData(1.5e15, "1.5e+15")]
    [InlineData(1234567890123456.0, "1.234567890123456e+15")]
    [InlineData(9007199254740992.0, "9.007199254740992e+15")]
    [InlineData(1e16, "1e+16")]
    [InlineData(12345678901234567.0, "1.2345678901234568e+16")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e100, "1e+100")]
    [InlineData(double.MaxValue, "1.7976931348623157e+308")]
    public void Floats_match_the_json_gem(double value, string json)
    {
        Assert.Equal(json, RailsJson.Generate(JsonValue.Create(value)));
        Assert.Equal($"[{json}]", RailsJson.Encode(new JsonArray(JsonValue.Create(value))));
    }

    [Fact]
    public void Non_finite_floats_are_null()
    {
        Assert.Equal("null", RailsJson.Encode(JsonValue.Create(double.NaN)));
        Assert.Equal("null", RailsJson.Encode(JsonValue.Create(double.PositiveInfinity)));
    }

    [Fact]
    public void Parsed_numbers_are_written_back_as_ruby_would()
    {
        Assert.True(RailsJson.TryParse("""{"a":1e16,"b":[1e-5,1.50],"c":9007199254740993,"d":-0}""", out var value));
        Assert.Equal("""{"a":1e+16,"b":[0.00001,1.5],"c":9007199254740993,"d":0}""", RailsJson.Encode(value));
    }

    [Fact]
    public void Repeated_keys_keep_their_place_and_take_the_last_value()
    {
        Assert.True(RailsJson.TryParse("""{"a":1,"b":2,"a":3}""", out var value));
        Assert.Equal("""{"a":3,"b":2}""", RailsJson.Generate(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[1,]")]
    [InlineData("nul")]
    public void Refuses_what_json_parse_refuses(string json) => Assert.False(RailsJson.TryParse(json, out _));

    [Fact]
    public void Parses_scalars_and_null()
    {
        Assert.True(RailsJson.TryParse(" null ", out var value));
        Assert.Null(value);
        Assert.True(RailsJson.TryParse("\"x\"", out value));
        Assert.Equal("x", value!.GetValue<string>());
    }
}

public class MessageMetadataTests
{
    [Fact]
    public void Iso8601_truncates_to_milliseconds()
    {
        Assert.Equal("2026-01-01T12:00:00.123Z", MessageMetadata.Iso8601Millis(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_239_990)));
        Assert.Equal("2046-01-01T12:00:00.000Z", MessageMetadata.Iso8601Millis(new DateTimeOffset(2046, 1, 1, 7, 0, 0, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void Envelopes_leave_out_what_is_unset_except_the_legacy_one()
    {
        var expiresAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("1"u8.ToArray(), MessageMetadata.Serialize(MessageSerializer.Json, JsonValue.Create(1), null, null));
        Assert.Equal("""{"_rails":{"data":1,"pur":"p"}}"""u8.ToArray(), MessageMetadata.Serialize(MessageSerializer.Json, JsonValue.Create(1), "p", null));
        Assert.Equal("""{"_rails":{"data":1,"exp":"2026-01-01T12:00:00.000Z"}}"""u8.ToArray(), MessageMetadata.Serialize(MessageSerializer.Json, JsonValue.Create(1), null, expiresAt));
        Assert.Equal("""{"_rails":{"message":"MQ==","exp":null,"pur":"p"}}"""u8.ToArray(), MessageMetadata.Serialize(MessageSerializer.Null, JsonValue.Create("1"), "p", null));
    }
}

public class SecurityUtilsTests
{
    [Fact]
    public void Secure_compare_compares_bytes()
    {
        Assert.True(SecurityUtils.SecureCompare("abc", "abc"));
        Assert.False(SecurityUtils.SecureCompare("abc", "abd"));
        Assert.False(SecurityUtils.SecureCompare("abc", "abcd"));
        Assert.True(SecurityUtils.SecureCompare("", ""));
        Assert.False(SecurityUtils.SecureCompare("é", "é"));
    }
}

public class KeyGeneratorTests
{
    [Fact]
    public void Caches_keys_without_sharing_them()
    {
        var keys = new KeyGenerator("secret");
        var first = keys.GenerateKey("salt");
        first[0] ^= 0xff;

        Assert.NotEqual(first, keys.GenerateKey("salt"));
        Assert.Equal(64, keys.GenerateKey("salt").Length);
        Assert.Equal(keys.GenerateKey("salt", 32), keys.GenerateKey("salt")[..32]);
    }
}
