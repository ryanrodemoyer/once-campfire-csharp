using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Crypto;

/// <summary>
/// The verifiers and encryptors the reference app builds from <c>secret_key_base</c>, configured
/// here only to exercise the primitives against <c>vectors/rails_compat.json</c>. The cookie jars,
/// signed ids and Turbo stream names themselves are C02's and C04's.
/// </summary>
static class RailsSecrets
{
    public static readonly KeyGenerator Keys = new(RailsCompatVectors.File.SecretKeyBase);

    public static DateTimeOffset Now => Time(RailsCompatVectors.File.Now);

    public static DateTimeOffset Time(string iso8601) =>
        DateTimeOffset.Parse(iso8601, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static DateTimeOffset? OptionalTime(string? iso8601) => iso8601 is null ? null : Time(iso8601);

    /// <summary><c>cookies.signed</c>: <c>generate_key("signed cookie")</c>, HMAC-SHA1 (<c>signed_cookie_digest</c> is unset).</summary>
    public static MessageVerifier SignedCookies() =>
        new(Keys.GenerateKey("signed cookie"), MessageDigest.Sha1, MessageEncoding.Strict, MessageSerializer.Null);

    /// <summary><c>cookies.encrypted</c>: <c>generate_key("authenticated encrypted cookie", 32)</c>, aes-256-gcm.</summary>
    public static MessageEncryptor EncryptedCookies() =>
        new(Keys.GenerateKey("authenticated encrypted cookie", 32), MessageSerializer.Null);

    /// <summary>
    /// <c>message_verifiers["active_record/signed_id"]</c> with the legacy options: SHA256, <c>::JSON</c>
    /// and URL-safe Base64, falling back to the app defaults when reading.
    /// </summary>
    public static MessageVerifier SignedIds()
    {
        var secret = Keys.GenerateKey("active_record/signed_id");
        return new MessageVerifier(secret, MessageDigest.Sha256, MessageEncoding.UrlSafe, MessageSerializer.Json)
            .FallBackTo(new MessageVerifier(secret, MessageDigest.Sha1, MessageEncoding.Strict, MessageSerializer.JsonAllowMarshal));
    }

    /// <summary><c>Turbo.signed_stream_verifier</c>: SHA256, <c>::JSON</c>, no envelope.</summary>
    public static MessageVerifier TurboStreams() =>
        new(Keys.GenerateKey("turbo/signed_stream_verifier_key"), MessageDigest.Sha256, MessageEncoding.Strict, MessageSerializer.Json);

    /// <summary><c>GlobalID::Verifier</c>: SHA1, URL-safe Base64 with padding, <c>:json_allow_marshal</c>.</summary>
    public static MessageVerifier SignedGlobalIds() =>
        new(Keys.GenerateKey("signed_global_ids"), MessageDigest.Sha1, MessageEncoding.UrlSafePadded, MessageSerializer.JsonAllowMarshal);

    /// <summary>A JSON vector value, written as Rails writes it, for comparing with what a primitive returns.</summary>
    public static string Json(JsonElement expected)
    {
        Assert.True(RailsJson.TryParse(expected.GetRawText(), out var node));
        return RailsJson.Generate(node);
    }

    public static string Json(JsonNode? value) => RailsJson.Generate(value);
}
