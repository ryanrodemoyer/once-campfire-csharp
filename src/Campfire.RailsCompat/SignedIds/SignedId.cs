using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Signing;

/// <summary>
/// <c>ActiveRecord::SignedId</c> (<c>signed_id(purpose:, expires_in:)</c> / <c>find_signed</c>).
/// The verifier uses key <c>generate_key("active_record/signed_id", 64)</c>, with legacy options
/// prepended (SHA256, URL-safe base64 unpadded, JSON, falling back to SHA1, strict base64, JSON allow marshal).
/// </summary>
public static class SignedId
{
    public const string Salt = "active_record/signed_id";

    /// <summary>Creates the verifier for Active Record signed IDs with legacy fallback.</summary>
    public static MessageVerifier CreateVerifier(KeyGenerator keys)
    {
        var secret = keys.GenerateKey(Salt, 64);
        var fallback = new MessageVerifier(secret, MessageDigest.Sha1, MessageEncoding.Strict, MessageSerializer.JsonAllowMarshal);
        return new MessageVerifier(secret, MessageDigest.Sha256, MessageEncoding.UrlSafe, MessageSerializer.Json).FallBackTo(fallback);
    }

    /// <summary>
    /// Generates a signed ID for a model record.
    /// <paramref name="modelName"/> is the record's base class name, e.g. "User" or "Rooms::Open".
    /// </summary>
    public static string Generate(KeyGenerator keys, string modelName, long id, string? purpose = null, DateTimeOffset? expiresAt = null)
    {
        var verifier = CreateVerifier(keys);
        var combinedPurpose = CombinePurposes(modelName, purpose);
        return verifier.Generate(JsonValue.Create(id), combinedPurpose, expiresAt);
    }

    /// <summary>
    /// Verifies a signed ID for a model record, returning the record ID or <c>null</c>.
    /// </summary>
    public static long? Verify(KeyGenerator keys, string modelName, string signedId, string? purpose, DateTimeOffset now)
    {
        var verifier = CreateVerifier(keys);
        var combinedPurpose = CombinePurposes(modelName, purpose);
        var result = verifier.Verify(signedId, combinedPurpose, now);
        if (!result.IsValid || result.Value is null)
        {
            return null;
        }

        if (result.Value is JsonValue jv)
        {
            if (jv.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (jv.TryGetValue<string>(out var s) && long.TryParse(s.Trim(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>combine_signed_id_purposes</c>: <c>[base_class.name.underscore, purpose.to_s].compact_blank.join("/")</c>.
    /// </summary>
    public static string CombinePurposes(string modelName, string? purpose)
    {
        var under = Underscore(modelName);
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return under;
        }
        return $"{under}/{purpose}";
    }

    /// <summary>
    /// <c>String#underscore</c> for class names: <c>Rooms::Open</c> → <c>rooms/open</c>, <c>WebPush</c> → <c>web_push</c>.
    /// </summary>
    public static string Underscore(string name)
    {
        name = name.Replace("::", "/");
        var chars = name.ToCharArray();
        var sb = new StringBuilder(name.Length + 5);
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsAsciiLetterUpper(c))
            {
                var prev = i > 0 ? (char?)chars[i - 1] : null;
                var next = i + 1 < chars.Length ? (char?)chars[i + 1] : null;
                var afterLowerOrDigit = prev.HasValue && (char.IsAsciiLetterLower(prev.Value) || char.IsAsciiDigit(prev.Value));
                var acronymEnd = prev.HasValue && char.IsAsciiLetterUpper(prev.Value) && next.HasValue && char.IsAsciiLetterLower(next.Value);
                if (afterLowerOrDigit || acronymEnd)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c == '-' ? '_' : c);
            }
        }
        return sb.ToString();
    }
}
