using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Crypto;

public enum MessageDigest
{
    Sha1,
    Sha256,
}

/// <summary>
/// How the payload is Base64-encoded when generating. Reading is lenient in every case:
/// <c>MessageVerifier#decode</c> retries with the other alphabet, and <c>GlobalID::Verifier</c>
/// uses <c>urlsafe_decode64</c>, which takes both.
/// </summary>
public enum MessageEncoding
{
    /// <summary><c>Base64.strict_encode64</c> (the default, <c>url_safe: false</c>).</summary>
    Strict,

    /// <summary><c>url_safe: true</c>: URL-safe alphabet without padding.</summary>
    UrlSafe,

    /// <summary><c>GlobalID::Verifier</c>: URL-safe alphabet with padding.</summary>
    UrlSafePadded,
}

/// <summary>
/// <c>ActiveSupport::MessageVerifier</c>: <c>&lt;base64 payload&gt;--&lt;hex HMAC of the base64 payload&gt;</c>.
/// Each use in Rails configures it differently, so callers pass every option.
/// </summary>
public sealed class MessageVerifier(byte[] secret, MessageDigest digest, MessageEncoding encoding, MessageSerializer serializer)
{
    readonly byte[] secret = (byte[])secret.Clone();
    readonly List<MessageVerifier> rotations = [];

    public MessageSerializer Serializer => serializer;

    /// <summary>
    /// <c>Rails.application.message_verifier(name)</c> with the app defaults: key
    /// <c>generate_key(name, 64)</c>, HMAC-SHA1, strict Base64, <c>:json_allow_marshal</c>, and the
    /// <c>_rails</c> envelope. Active Storage uses <c>"ActiveStorage"</c>.
    /// </summary>
    public static MessageVerifier ForApp(KeyGenerator keys, string name) =>
        new(keys.GenerateKey(name), MessageDigest.Sha1, MessageEncoding.Strict, MessageSerializer.JsonAllowMarshal);

    /// <summary><c>rotate</c> / <c>fall_back_to</c>: tried in order when this verifier can't read a message.</summary>
    public MessageVerifier FallBackTo(MessageVerifier rotation)
    {
        rotations.Add(rotation);
        return this;
    }

    /// <summary><c>generate(value, purpose:, expires_at:)</c>.</summary>
    public string Generate(JsonNode? value, string? purpose = null, DateTimeOffset? expiresAt = null) =>
        Sign(MessageMetadata.Serialize(serializer, value, purpose, expiresAt));

    /// <summary>
    /// <see cref="Generate"/> for data the caller already dumped with this verifier's serializer
    /// (JSON for the JSON serializers, the string itself for <see cref="MessageSerializer.Null"/>),
    /// so key order and escaping are under the caller's control.
    /// </summary>
    public string GenerateRaw(string dumped, string? purpose = null, DateTimeOffset? expiresAt = null) =>
        Sign(MessageMetadata.SerializeDumped(serializer, dumped, purpose, expiresAt));

    /// <summary><c>verified</c> / <c>verify</c>: the value, or why it couldn't be read.</summary>
    public MessageResult<JsonNode> Verify(string message, string? purpose, DateTimeOffset now)
    {
        var result = Read(message, purpose, now);
        if (!result.Rotates)
        {
            return result;
        }
        foreach (var rotation in rotations)
        {
            var rotated = rotation.Read(message, purpose, now);
            if (!rotated.Rotates)
            {
                return rotated;
            }
        }
        return result;
    }

    /// <summary>
    /// <see cref="Verify"/>, returning the data dumped again with this verifier's serializer. Key
    /// order is kept; escapes and number layout are normalized.
    /// </summary>
    public MessageResult<string> VerifyRaw(string message, string? purpose, DateTimeOffset now) =>
        Verify(message, purpose, now).Map(value => serializer.DumpString(value));

    string Sign(byte[] serialized)
    {
        var encoded = encoding switch
        {
            MessageEncoding.Strict => RubyBase64.StrictEncode(serialized),
            MessageEncoding.UrlSafe => RubyBase64.UrlSafeEncode(serialized, padding: false),
            _ => RubyBase64.UrlSafeEncode(serialized, padding: true),
        };
        return $"{encoded}--{HexDigest(encoded)}";
    }

    MessageResult<JsonNode> Read(string message, string? purpose, DateTimeOffset now)
    {
        if (ExtractEncoded(message) is not { } encoded || RubyBase64.UrlSafeDecode(encoded) is not { } decoded)
        {
            return MessageResult.Fail<JsonNode>(MessageError.InvalidSignature);
        }
        return MessageMetadata.Deserialize(serializer, decoded, purpose, now, RubyBase64.UrlSafeDecode);
    }

    /// <summary><c>extract_encoded</c>: the digest is the last <c>2 * digest_length</c> characters, after <c>--</c>.</summary>
    string? ExtractEncoded(string signed)
    {
        var index = signed.Length - HexLength - 2;
        if (index < 0 || string.CompareOrdinal(signed, index, "--", 0, 2) != 0)
        {
            return null;
        }
        var (encoded, hex) = (signed[..index], signed[(index + 2)..]);
        // `data.present? && digest.present?`
        if (string.IsNullOrWhiteSpace(encoded) || string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        return SecurityUtils.SecureCompare(hex, HexDigest(encoded)) ? encoded : null;
    }

    int HexLength => digest == MessageDigest.Sha1 ? 40 : 64;

    string HexDigest(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        // Rails still signs cookies and app messages with HMAC-SHA1, so reading them needs it too.
#pragma warning disable CA5350
        var mac = digest == MessageDigest.Sha1 ? HMACSHA1.HashData(secret, bytes) : HMACSHA256.HashData(secret, bytes);
#pragma warning restore CA5350
        return Convert.ToHexStringLower(mac);
    }
}
