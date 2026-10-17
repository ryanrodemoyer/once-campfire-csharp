using System.Buffers;
using System.Buffers.Text;
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

        if (serializer == MessageSerializer.Null)
        {
            return SerializeLegacy(dumped, purpose, expiresAt) ?? SerializeLegacySlowly(dumped, purpose, expiresAt);
        }

        var expiry = expiresAt is { } time ? JsonValue.Create(Iso8601Millis(time)) : null;
        var pur = purpose is null ? null : JsonValue.Create(purpose);

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

    static ReadOnlySpan<byte> LegacyExpiryKey => ",\"exp\":"u8;

    static ReadOnlySpan<byte> LegacyPurposeKey => ",\"pur\":"u8;

    /// <summary>
    /// The legacy envelope as bytes, written straight into the array that is returned: it is the plaintext of every
    /// session cookie and the payload of every signed cookie a response sets. Null when the purpose has something
    /// <see cref="RailsJson"/> would escape or isn't ASCII (none do), which leaves it to <see cref="SerializeLegacySlowly"/>.
    /// </summary>
    static byte[]? SerializeLegacy(string dumped, string? purpose, DateTimeOffset? expiresAt)
    {
        if (purpose is not null && !IsPlainJsonText(purpose))
        {
            return null;
        }

        var maxBytes = Encoding.UTF8.GetMaxByteCount(dumped.Length);
        byte[]? rented = null;
        Span<byte> dumpedBytes = maxBytes <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var dumpedLength = Encoding.UTF8.GetBytes(dumped, dumpedBytes);
            var base64Length = Base64.GetMaxEncodedToUtf8Length(dumpedLength);

            Span<char> expiry = stackalloc char[32];
            var expiryLength = 0;
            if (expiresAt is { } time && !time.UtcDateTime.TryFormat(expiry, out expiryLength, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            {
                return null;
            }

            var length = LegacyEnvelopePrefix.Length + 1 + base64Length + 1 + LegacyExpiryKey.Length
                + (expiresAt is null ? 4 : expiryLength + 2) + LegacyPurposeKey.Length + (purpose is null ? 4 : purpose.Length + 2) + 2;
            var envelope = new byte[length];
            var at = 0;
            LegacyEnvelopePrefix.CopyTo(envelope);
            at += LegacyEnvelopePrefix.Length;
            envelope[at++] = (byte)'"';
            Base64.EncodeToUtf8(dumpedBytes[..dumpedLength], envelope.AsSpan(at), out _, out var written);
            at += written;
            envelope[at++] = (byte)'"';
            LegacyExpiryKey.CopyTo(envelope.AsSpan(at));
            at += LegacyExpiryKey.Length;
            at = expiresAt is null ? WriteNull(envelope, at) : WriteQuoted(envelope, at, expiry[..expiryLength]);
            LegacyPurposeKey.CopyTo(envelope.AsSpan(at));
            at += LegacyPurposeKey.Length;
            at = purpose is null ? WriteNull(envelope, at) : WriteQuoted(envelope, at, purpose);
            envelope[at++] = (byte)'}';
            envelope[at++] = (byte)'}';
            return at == length ? envelope : null;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    static int WriteNull(Span<byte> to, int at)
    {
        "null"u8.CopyTo(to[at..]);
        return at + 4;
    }

    static int WriteQuoted(Span<byte> to, int at, ReadOnlySpan<char> text)
    {
        to[at++] = (byte)'"';
        foreach (var c in text)
        {
            to[at++] = (byte)c;
        }
        to[at++] = (byte)'"';
        return at;
    }

    // ASCII with none of what RailsJson escapes (quote, backslash, controls, the HTML three): written between its quotes as it is.
    static bool IsPlainJsonText(string text)
    {
        foreach (var c in text)
        {
            if (c is < ' ' or > '~' or '"' or '\\' or '<' or '>' or '&')
            {
                return false;
            }
        }
        return true;
    }

    static byte[] SerializeLegacySlowly(string dumped, string? purpose, DateTimeOffset? expiresAt)
    {
        var expiry = expiresAt is { } time ? JsonValue.Create(Iso8601Millis(time)) : null;
        var pur = purpose is null ? null : JsonValue.Create(purpose);
        var message = JsonValue.Create(RubyBase64.StrictEncode(Encoding.UTF8.GetBytes(dumped)));
        return Encoding.UTF8.GetBytes(
            $"{{\"_rails\":{{\"message\":{RailsJson.Encode(message)},\"exp\":{RailsJson.Encode(expiry)},\"pur\":{RailsJson.Encode(pur)}}}}}");
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
            if (TryReadLegacy(bytes, out var packed))
            {
                var problem = CheckLegacy(bytes, packed, purpose, now);
                if (problem != MessageError.None)
                {
                    return MessageResult.Fail<JsonNode>(problem);
                }
                return decodeLegacyMessage(Encoding.UTF8.GetString(bytes[packed.Message])) is { } packedDumped
                    ? serializer.Load(packedDumped)
                    : MessageResult.Fail<JsonNode>(MessageError.InvalidSignature);
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

    /// <summary>Where the legacy envelope's three values sit in the bytes it was read from.</summary>
    readonly record struct LegacyPacked(Range Message, Range Expiry, bool HasExpiry, Range Purpose, bool HasPurpose);

    /// <summary>
    /// Reads <c>{"_rails":{"message":"..","exp":null|"..","pur":null|".."}}</c> exactly as Rails and
    /// <see cref="SerializeLegacy"/> write it, with plain printable strings. Anything else (a space, another order, an
    /// escape, another key, a byte outside ASCII) is left to the parser that builds a JsonNode, so it reads whatever
    /// that read.
    /// </summary>
    static bool TryReadLegacy(ReadOnlySpan<byte> bytes, out LegacyPacked packed)
    {
        packed = default;
        var at = LegacyEnvelopePrefix.Length;
        if (!TryReadString(bytes, ref at, out var message) || !bytes[at..].StartsWith(LegacyExpiryKey))
        {
            return false;
        }
        at += LegacyExpiryKey.Length;
        var hasExpiry = TryReadString(bytes, ref at, out var expiry);
        if (!hasExpiry && !TryReadNull(bytes, ref at))
        {
            return false;
        }
        if (!bytes[at..].StartsWith(LegacyPurposeKey))
        {
            return false;
        }
        at += LegacyPurposeKey.Length;
        var hasPurpose = TryReadString(bytes, ref at, out var purpose);
        if (!hasPurpose && !TryReadNull(bytes, ref at))
        {
            return false;
        }
        if (!bytes[at..].SequenceEqual("}}"u8))
        {
            return false;
        }
        packed = new LegacyPacked(message, expiry, hasExpiry, purpose, hasPurpose);
        return true;
    }

    // A string of printable ASCII with no escape, up to its closing quote; nothing moves when it isn't one.
    static bool TryReadString(ReadOnlySpan<byte> bytes, ref int at, out Range text)
    {
        text = default;
        if (at >= bytes.Length || bytes[at] != (byte)'"')
        {
            return false;
        }
        var body = bytes[(at + 1)..];
        var end = body.IndexOfAny((byte)'"', (byte)'\\');
        if (end < 0 || body[end] != (byte)'"' || body[..end].IndexOfAnyExceptInRange((byte)' ', (byte)'~') >= 0)
        {
            return false;
        }
        text = (at + 1)..(at + 1 + end);
        at += end + 2;
        return true;
    }

    static bool TryReadNull(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (!bytes[at..].StartsWith("null"u8))
        {
            return false;
        }
        at += 4;
        return true;
    }

    /// <summary><see cref="Extract"/> for the envelope read without a node.</summary>
    static MessageError CheckLegacy(ReadOnlySpan<byte> bytes, LegacyPacked packed, string? purpose, DateTimeOffset now)
    {
        if (packed.HasExpiry)
        {
            var expiry = bytes[packed.Expiry];
            Span<char> text = stackalloc char[64];
            if (expiry.Length > text.Length)
            {
                return MessageError.InvalidMessage;
            }
            var length = Encoding.ASCII.GetChars(expiry, text);
            if (!DateTimeOffset.TryParseExact(text[..length], Iso8601Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
            {
                return MessageError.InvalidMessage;
            }
            if (now >= expiresAt)
            {
                return MessageError.Expired;
            }
        }

        var wanted = purpose ?? "";
        var matches = packed.HasPurpose ? IsAsciiOf(wanted, bytes[packed.Purpose]) : wanted.Length == 0;
        return matches ? MessageError.None : MessageError.PurposeMismatch;
    }

    static bool IsAsciiOf(string text, ReadOnlySpan<byte> bytes)
    {
        if (text.Length != bytes.Length)
        {
            return false;
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != bytes[i])
            {
                return false;
            }
        }
        return true;
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
