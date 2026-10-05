using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Signing;

/// <summary>
/// <c>Turbo::StreamsChannel.signed_stream_name</c> / <c>verified_stream_name</c>
/// (<c>turbo-rails app/channels/turbo/streams/stream_name.rb</c>).
/// The verifier uses key <c>generate_key("turbo/signed_stream_verifier_key")</c>, HMAC-SHA256,
/// strict Base64 of the JSON-dumped stream name, without a metadata envelope.
/// </summary>
public static class TurboStreamName
{
    public const string Salt = "turbo/signed_stream_verifier_key";

    public static MessageVerifier CreateVerifier(KeyGenerator keys)
    {
        var secret = keys.GenerateKey(Salt, 64);
        return new MessageVerifier(secret, MessageDigest.Sha256, MessageEncoding.Strict, MessageSerializer.Json);
    }

    /// <summary>
    /// Signs streamables joined with <c>:</c>.
    /// E.g. <c>turbo_stream_from @room, :messages</c> joins room GID param and "messages".
    /// </summary>
    public static string SignedStreamName(KeyGenerator keys, IEnumerable<string> streamables)
    {
        var streamName = string.Join(":", streamables);
        return CreateVerifier(keys).Generate(JsonValue.Create(streamName), null, null);
    }

    /// <summary>
    /// Signs streamables joined with <c>:</c>.
    /// </summary>
    public static string SignedStreamName(KeyGenerator keys, params string[] streamables) =>
        SignedStreamName(keys, (IEnumerable<string>)streamables);

    /// <summary>
    /// Verifies the signed stream name, returning the stream name or <c>null</c>.
    /// </summary>
    public static string? VerifiedStreamName(KeyGenerator keys, string signedStream)
    {
        var result = CreateVerifier(keys).Verify(signedStream, null, DateTimeOffset.UnixEpoch);
        if (!result.IsValid || result.Value is null)
        {
            return null;
        }

        if (result.Value is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var str))
            {
                return str;
            }
            return jv.ToString();
        }

        return null;
    }
}
