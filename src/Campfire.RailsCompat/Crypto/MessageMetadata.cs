using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// <c>ActiveSupport::Messages::Metadata</c>: how a value and its purpose and expiry are packed into
/// the bytes that get signed or encrypted.
/// </summary>
/// <remarks>
/// There are two envelopes. With a serializer Rails trusts for metadata (JSON, or a JSON fallback
/// serializer) it is <c>{"_rails":{"data":&lt;value&gt;,"exp":..,"pur":..}}</c>, leaving out what
/// isn't set. With the cookie jars' <see cref="MessageSerializer.Null"/> it is the legacy
/// "dual-serialized" one, <c>{"_rails":{"message":"&lt;base64 of the dumped value&gt;","exp":..,"pur":..}}</c>,
/// which always carries <c>exp</c> and <c>pur</c>, as <c>null</c> when unset.
/// </remarks>
public static class MessageMetadata
{
    static readonly byte[] LegacyEnvelopePrefix = "{\"_rails\":{\"message\":"u8.ToArray();

    /// <summary><c>Time#iso8601(3)</c> in UTC: <c>2046-01-01T12:00:00.000Z</c> (truncated, not rounded).</summary>
    public static string Iso8601Millis(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static byte[] Serialize(MessageSerializer serializer, JsonNode? value, string? purpose, DateTimeOffset? expiresAt) =>
        SerializeDumped(serializer, serializer.DumpString(value), purpose, expiresAt);

    /// <summary>Like <see cref="Serialize"/>, for a value the caller already dumped with <paramref name="serializer"/>.</summary>
    internal static byte[] SerializeDumped(MessageSerializer serializer, string dumped, string? purpose, DateTimeOffset? expiresAt)
    {
        if (purpose is null && expiresAt is null)
        {
            return Encoding.UTF8.GetBytes(dumped);
        }

        var expiry = expiresAt is { } time ? JsonValue.Create(Iso8601Millis(time)) : null;
        var pur = purpose is null ? null : JsonValue.Create(purpose);

        if (serializer == MessageSerializer.Null)
        {
            var message = JsonValue.Create(RubyBase64.StrictEncode(Encoding.UTF8.GetBytes(dumped)));
            return Encoding.UTF8.GetBytes(
                $"{{\"_rails\":{{\"message\":{RailsJson.Encode(message)},\"exp\":{RailsJson.Encode(expiry)},\"pur\":{RailsJson.Encode(pur)}}}}}");
        }

        var envelope = new StringBuilder("{\"_rails\":{\"data\":").Append(dumped);
        if (expiry is not null)
        {
            envelope.Append(",\"exp\":").Append(serializer.EncodeJson(expiry));
        }
        if (pur is not null)
        {
            envelope.Append(",\"pur\":").Append(serializer.EncodeJson(pur));
        }
        return Encoding.UTF8.GetBytes(envelope.Append("}}").ToString());
    }

    /// <summary>
    /// <c>deserialize_with_metadata</c>. <paramref name="decodeLegacyMessage"/> decodes the Base64
    /// inside a legacy envelope: the verifier takes either alphabet, the encryptor only strict Base64.
    /// </summary>
    internal static MessageResult<JsonNode> Deserialize(
        MessageSerializer serializer, ReadOnlySpan<byte> bytes, string? purpose, DateTimeOffset now, Func<string, byte[]?> decodeLegacyMessage)
    {
        if (bytes.StartsWith(LegacyEnvelopePrefix))
        {
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

        var loaded = serializer.Load(bytes);
        if (!loaded.IsValid)
        {
            return loaded;
        }
        if (loaded.Value is JsonObject obj && obj.ContainsKey("_rails"))
        {
            var extracted = Extract(obj, purpose, now, out var rails);
            return extracted == MessageError.None
                ? MessageResult.Ok<JsonNode>(rails!["data"]?.DeepClone())
                : MessageResult.Fail<JsonNode>(extracted);
        }
        return purpose is null ? loaded : MessageResult.Fail<JsonNode>(MessageError.PurposeMismatch);
    }

    /// <summary>
    /// <c>extract_from_metadata_envelope</c>: expired when <c>now &gt;= exp</c>. Purposes compare
    /// with <c>to_s</c>, so a missing <c>pur</c> matches no purpose.
    /// </summary>
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
                if (!TryParseIso8601(text, out var expiresAt))
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

    static readonly string[] Iso8601Formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    static bool TryParseIso8601(string text, out DateTimeOffset time) =>
        DateTimeOffset.TryParseExact(text, Iso8601Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);

    /// <summary><c>Object#to_s</c> for the JSON values a purpose could hold.</summary>
    static string RubyToS(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => RailsJson.Generate(v),
        // Arrays and hashes to_s as their #inspect, which no purpose we compare against looks like.
        _ => "\0" + RailsJson.Generate(value),
    };
}
