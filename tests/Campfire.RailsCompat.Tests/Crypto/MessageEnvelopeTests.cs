using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Tests.Crypto;

/// <summary>
/// The envelope the cookie jars put a value in, <c>{"_rails":{"message":"..","exp":..,"pur":..}}</c>
/// (<c>ActiveSupport::Messages::Metadata</c>): the plaintext of every session cookie and the payload of every signed
/// cookie. It is written and read without building JSON nodes; <see cref="Before"/> is the code that did, which the
/// Rails vectors in the crypto tests already checked, and the two must agree on every envelope, the ones Rails writes
/// and the odd ones the fast reader leaves to the parser.
/// </summary>
public class MessageEnvelopeTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-02T16:00:00Z", CultureInfo.InvariantCulture);

    static readonly string Accent = ((char)0xe9).ToString();

    static readonly string[] Dumped =
    [
        "\"x\"",
        "{\"user_id\":1,\"csrf\":\"abc\"}",
        "",
        new string('a', 900),
        "\"" + new string('b', 3000) + "\"",
        "{\"name\":\"caf" + Accent + "\"}",
    ];

    static readonly string?[] Purposes =
    [
        null, "", "cookie.session_token", "cookie._campfire_session", "a<b", "quote\"d", "back\\slash", "caf" + Accent, new string('p', 300), "line\nbreak",
    ];

    static readonly DateTimeOffset?[] Expiries =
    [
        null,
        Now.AddYears(20),
        new DateTimeOffset(2046, 10, 14, 17, 5, 41, 657, TimeSpan.Zero),
        Now.ToOffset(TimeSpan.FromHours(-6)).AddMilliseconds(999.9),
        DateTimeOffset.UnixEpoch,
    ];

    [Fact]
    public void An_envelope_is_written_as_before()
    {
        foreach (var dumped in Dumped)
        {
            foreach (var purpose in Purposes)
            {
                foreach (var expiresAt in Expiries)
                {
                    Assert.Equal(
                        Before.Serialize(dumped, purpose, expiresAt),
                        MessageMetadata.SerializeDumped(MessageSerializer.Null, dumped, purpose, expiresAt));
                }
            }
        }
    }

    [Fact]
    public void A_session_envelope_is_written_into_one_array()
    {
        var dumped = "{\"user_id\":1,\"csrf\":\"" + new string('c', 40) + "\",\"flash\":{\"discard\":[],\"flashes\":{}}}";
        var expiresAt = Now.AddYears(20);
        var envelope = MessageMetadata.SerializeDumped(MessageSerializer.Null, dumped, "cookie._campfire_session", expiresAt);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            envelope = MessageMetadata.SerializeDumped(MessageSerializer.Null, dumped, "cookie._campfire_session", expiresAt);
        }
        var each = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // The array (a header of 24 bytes and its length) and nothing else, give or take a little.
        Assert.InRange(each, 0, envelope.Length + 24 + 64);
    }

    [Fact]
    public void Every_envelope_is_read_as_before()
    {
        var wanted = new string?[] { null, "", "cookie.session_token", "cookie._campfire_session", "other" };
        var nows = new[] { Now, Now.AddYears(30) };
        var read = 0;
        foreach (var dumped in Dumped)
        {
            foreach (var purpose in Purposes)
            {
                foreach (var expiresAt in Expiries)
                {
                    if (purpose is null && expiresAt is null)
                    {
                        // The value as it is, not an envelope.
                        continue;
                    }
                    var bytes = MessageMetadata.SerializeDumped(MessageSerializer.Null, dumped, purpose, expiresAt);
                    foreach (var want in wanted)
                    {
                        foreach (var now in nows)
                        {
                            AssertSame(bytes, want, now);
                            read++;
                        }
                    }
                }
            }
        }
        Assert.True(read > 2_000);
    }

    // Written by hand: the shapes Rails writes with the odd ones around them.
    static IEnumerable<string> Handmade()
    {
        const string hi = "ImhpIg==";
        var future = "2046-10-14T17:05:41.657Z";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":\"{future}\",\"pur\":\"cookie.x\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":\"2046-10-14T17:05:41Z\",\"pur\":\"cookie.x\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":\"2046-10-14T17:05:41.657+00:00\",\"pur\":\"cookie.x\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":\"2020-01-01T00:00:00.000Z\",\"pur\":\"cookie.x\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":\"not a time\",\"pur\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":123,\"pur\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":5}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":\"\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":\"a\\\"b\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"pur\":null,\"exp\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":null,\"extra\":1}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"exp\":\"{future}\",\"pur\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\", \"exp\":null,\"pur\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":null}}}} ";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":null}}";
        yield return "{\"_rails\":{\"message\":\"!!!\",\"exp\":null,\"pur\":null}}";
        yield return "{\"_rails\":{\"message\":\"\",\"exp\":null,\"pur\":null}}";
        yield return "{\"_rails\":{\"message\":null,\"exp\":null,\"pur\":null}}";
        yield return "{\"_rails\":{\"message\":7,\"exp\":null,\"pur\":null}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\\n\",\"exp\":null,\"pur\":null}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":\"caf{Accent}\"}}}}";
        yield return $"{{\"_rails\":{{\"message\":\"{hi}\",\"exp\":null,\"pur\":\"a\tb\"}}}}";
    }

    [Fact]
    public void Odd_envelopes_are_read_as_before()
    {
        foreach (var json in Handmade())
        {
            foreach (var want in new string?[] { null, "", "cookie.x", "a\"b" })
            {
                foreach (var now in new[] { Now, Now.AddYears(30) })
                {
                    AssertSame(Encoding.UTF8.GetBytes(json), want, now);
                }
            }
        }
    }

    [Fact]
    public void A_bad_byte_is_read_as_before()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"_rails\":{\"message\":\"ImhpIg==\",\"exp\":null,\"pur\":\"x\"}}");
        // From after the prefix that makes it a legacy envelope.
        for (var at = "{\"_rails\":{\"message\":".Length; at < bytes.Length; at++)
        {
            foreach (var replacement in new byte[] { 0, 0x20, 0x22, 0x5c, 0x7b, 0xc3, 0xff })
            {
                var changed = (byte[])bytes.Clone();
                changed[at] = replacement;
                AssertSame(changed, "x", Now);
            }
        }
    }

    static void AssertSame(byte[] bytes, string? purpose, DateTimeOffset now)
    {
        var expected = Describe(() => Before.Deserialize(MessageSerializer.Null, bytes, purpose, now, RubyBase64.StrictDecode));
        var actual = Describe(() => MessageMetadata.Deserialize(MessageSerializer.Null, bytes, purpose, now, RubyBase64.StrictDecode));
        Assert.True(expected == actual, $"{Encoding.UTF8.GetString(bytes)} for {purpose ?? "null"}: before {expected}, now {actual}");
    }

    // What came back, or that it raised: a byte that isn't UTF-8 makes the JSON parser throw, and the fast reader leaves those to it.
    static string Describe(Func<MessageResult<JsonNode>> read)
    {
        try
        {
            var result = read();
            return $"{result.Error} {result.Value?.ToJsonString()}";
        }
        catch (Exception e)
        {
            return "raised " + e.GetType().Name;
        }
    }

    /// <summary>The envelope as it was written and read, through JsonNode.</summary>
    static class Before
    {
        public static byte[] Serialize(string dumped, string? purpose, DateTimeOffset? expiresAt)
        {
            if (purpose is null && expiresAt is null)
            {
                return Encoding.UTF8.GetBytes(dumped);
            }
            var expiry = expiresAt is { } time ? JsonValue.Create(MessageMetadata.Iso8601Millis(time)) : null;
            var pur = purpose is null ? null : JsonValue.Create(purpose);
            var message = JsonValue.Create(RubyBase64.StrictEncode(Encoding.UTF8.GetBytes(dumped)));
            return Encoding.UTF8.GetBytes(
                $"{{\"_rails\":{{\"message\":{RailsJson.Encode(message)},\"exp\":{RailsJson.Encode(expiry)},\"pur\":{RailsJson.Encode(pur)}}}}}");
        }

        public static MessageResult<JsonNode> Deserialize(
            MessageSerializer serializer, ReadOnlySpan<byte> bytes, string? purpose, DateTimeOffset now, Func<string, byte[]?> decodeLegacyMessage)
        {
            if (!bytes.StartsWith("{\"_rails\":{\"message\":"u8))
            {
                throw new InvalidOperationException("not a legacy envelope");
            }
            if (!RailsJson.TryParse(bytes, out var envelope))
            {
                return MessageResult.Fail<JsonNode>(MessageError.InvalidSignature);
            }
            var extracted = Extract(envelope, purpose, now, out var rails);
            if (extracted != MessageError.None)
            {
                return MessageResult.Fail<JsonNode>(extracted);
            }
            if (rails!["message"] is not JsonValue message || !message.TryGetValue<string>(out var encoded)
                || decodeLegacyMessage(encoded) is not { } dumped)
            {
                return MessageResult.Fail<JsonNode>(MessageError.InvalidSignature);
            }
            return serializer.Load(dumped);
        }

        static MessageError Extract(JsonNode? envelope, string? purpose, DateTimeOffset now, out JsonObject? rails)
        {
            rails = (envelope as JsonObject)?["_rails"] as JsonObject;
            if (rails is null)
            {
                return MessageError.InvalidMessage;
            }

            switch (rails["exp"])
            {
                case null:
                    break;
                case JsonValue exp when exp.TryGetValue<string>(out var text):
                    if (!DateTimeOffset.TryParseExact(
                            text, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
                    {
                        return MessageError.InvalidMessage;
                    }
                    if (now >= expiresAt)
                    {
                        return MessageError.Expired;
                    }
                    break;
                default:
                    return MessageError.InvalidMessage;
            }

            return RubyToS(rails["pur"]) == (purpose ?? "") ? MessageError.None : MessageError.PurposeMismatch;
        }

        static string RubyToS(JsonNode? value) => value switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => RailsJson.Generate(v),
            _ => "\0" + RailsJson.Generate(value),
        };
    }
}
