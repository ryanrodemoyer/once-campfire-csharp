using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.GlobalId;

/// <summary>
/// <c>GlobalID / SignedGlobalID</c> (<c>gid://campfire/User/1</c>, <c>user.attachable_sgid</c>).
/// SGIDs are signed by <c>GlobalID::Verifier</c> (key <c>generate_key("signed_global_ids")</c>,
/// HMAC-SHA1, URL-safe Base64 with padding, <c>:json_allow_marshal</c>, and the <c>_rails</c> envelope).
/// </summary>
public static class SignedGlobalId
{
    public const string Salt = "signed_global_ids";
    public const string App = "campfire";
    public const string AttachablePurpose = "attachable";
    public const string DefaultPurpose = "default";

    public static MessageVerifier CreateVerifier(KeyGenerator keys)
    {
        var secret = keys.GenerateKey(Salt, 64);
        return new MessageVerifier(secret, MessageDigest.Sha1, MessageEncoding.UrlSafePadded, MessageSerializer.JsonAllowMarshal);
    }

    /// <summary>
    /// <c>record.attachable_sgid</c>, i.e. <c>to_sgid(expires_in: nil, for: "attachable")</c>.
    /// GlobalID turns the leftover <c>expires_in: nil</c> option into a query param, so the signed
    /// data is <c>gid://campfire/User/1?expires_in</c>, with no expiry in the envelope.
    /// </summary>
    public static string AttachableSgid(KeyGenerator keys, GlobalId gid)
    {
        var verifier = CreateVerifier(keys);
        return verifier.Generate(JsonValue.Create($"{gid}?expires_in"), AttachablePurpose, null);
    }

    /// <summary>
    /// <c>SignedGlobalID.new(gid_uri, for: purpose, expires_at:)</c> for a bare GID URI.
    /// </summary>
    public static string Generate(KeyGenerator keys, GlobalId gid, string purpose, DateTimeOffset? expiresAt = null)
    {
        var verifier = CreateVerifier(keys);
        return verifier.Generate(JsonValue.Create(gid.ToString()), purpose, expiresAt);
    }

    /// <summary>
    /// <c>SignedGlobalID.parse(sgid, for: purpose)</c>: returns the <see cref="GlobalId"/> if
    /// signature, purpose, and expiry are valid.
    /// </summary>
    public static GlobalId? LocateSigned(KeyGenerator keys, string sgid, string purpose, DateTimeOffset now)
    {
        var verifier = CreateVerifier(keys);
        var result = verifier.Verify(sgid, purpose, now);
        JsonNode? data;
        if (result.IsValid)
        {
            data = result.Value;
        }
        else
        {
            data = VerifyWithLegacySelfValidatedMetadata(verifier, sgid, purpose, now);
            if (data is null)
            {
                return null;
            }
        }

        if (data is JsonValue jv && jv.TryGetValue<string>(out var uri))
        {
            return GlobalId.Parse(uri) ?? GlobalId.FromParam(uri);
        }

        return null;
    }

    /// <summary>
    /// globalid &lt; 1.0 signed <c>{"gid":..,"purpose":..,"expires_at":..}</c> without a Rails envelope.
    /// </summary>
    static JsonNode? VerifyWithLegacySelfValidatedMetadata(MessageVerifier verifier, string sgid, string purpose, DateTimeOffset now)
    {
        var result = verifier.Verify(sgid, null, now);
        if (!result.IsValid || result.Value is not JsonObject metadata)
        {
            return null;
        }

        if (metadata.TryGetPropertyValue("expires_at", out var expNode) && expNode is not null && expNode is JsonValue expVal)
        {
            if (expVal.TryGetValue<string>(out var expStr) && DateTimeOffset.TryParse(expStr, out var expTime))
            {
                if (now > expTime)
                {
                    return null;
                }
            }
        }

        if (metadata.TryGetPropertyValue("purpose", out var purNode) && purNode is not null)
        {
            var purStr = purNode.ToString();
            if (string.Equals(purStr, purpose, StringComparison.Ordinal))
            {
                return metadata.TryGetPropertyValue("gid", out var gidNode) ? gidNode : null;
            }
        }

        return null;
    }
}
