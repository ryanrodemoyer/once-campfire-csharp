using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// <c>ActiveSupport::MessageEncryptor</c> with <c>aes-256-gcm</c>, as the encrypted cookie jar
/// builds it: <c>&lt;base64 ciphertext&gt;--&lt;base64 12-byte IV&gt;--&lt;base64 16-byte auth tag&gt;</c>,
/// strict Base64, empty auth data, no separate signature.
/// </summary>
public sealed class MessageEncryptor
{
    const int ivLength = 12;
    const int authTagLength = 16;
    // Strict Base64 lengths of the IV and auth tag.
    const int encodedIvLength = 16;
    const int encodedAuthTagLength = 24;

    readonly byte[] secret;
    readonly MessageSerializer serializer;

    /// <summary>The <paramref name="secret"/> is 32 bytes: <c>key_generator.generate_key(salt, 32)</c>.</summary>
    public MessageEncryptor(byte[] secret, MessageSerializer serializer)
    {
        if (secret.Length != 32)
        {
            throw new ArgumentException("aes-256-gcm needs a 32-byte key", nameof(secret));
        }
        this.secret = (byte[])secret.Clone();
        this.serializer = serializer;
    }

    /// <summary><c>encrypt_and_sign(value, purpose:, expires_at:)</c> with a random IV.</summary>
    public string EncryptAndSign(JsonNode? value, string? purpose = null, DateTimeOffset? expiresAt = null) =>
        Encrypt(MessageMetadata.Serialize(serializer, value, purpose, expiresAt), RandomNumberGenerator.GetBytes(ivLength));

    /// <summary><see cref="EncryptAndSign"/> for a value the caller already dumped with this encryptor's serializer.</summary>
    public string EncryptAndSignRaw(string dumped, string? purpose = null, DateTimeOffset? expiresAt = null) =>
        Encrypt(MessageMetadata.SerializeDumped(serializer, dumped, purpose, expiresAt), RandomNumberGenerator.GetBytes(ivLength));

    /// <summary><c>decrypt_and_verify</c>: the value, or why it couldn't be read.</summary>
    public MessageResult<JsonNode> DecryptAndVerify(string message, string? purpose, DateTimeOffset now) =>
        Decrypt(message) is { } plaintext
            ? MessageMetadata.Deserialize(serializer, plaintext, purpose, now, RubyBase64.StrictDecode)
            : MessageResult.Fail<JsonNode>(MessageError.InvalidSignature);

    /// <summary>The decrypted bytes, before any envelope handling, or <c>null</c>.</summary>
    public byte[]? Decrypt(string message)
    {
        if (ExtractParts(message) is not var (encodedCiphertext, encodedIv, encodedTag)
            || RubyBase64.StrictDecode(encodedCiphertext) is not { } ciphertext
            || RubyBase64.StrictDecode(encodedIv) is not { Length: ivLength } iv
            || RubyBase64.StrictDecode(encodedTag) is not { Length: authTagLength } tag)
        {
            return null;
        }
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(secret, authTagLength);
        try
        {
            aes.Decrypt(iv, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    internal string Encrypt(byte[] plaintext, byte[] iv)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[authTagLength];
        using (var aes = new AesGcm(secret, authTagLength))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag);
        }
        return $"{RubyBase64.StrictEncode(ciphertext)}--{RubyBase64.StrictEncode(iv)}--{RubyBase64.StrictEncode(tag)}";
    }

    /// <summary><c>extract_parts</c>: a fixed-length IV and auth tag at the end, each after <c>--</c>.</summary>
    static (string Ciphertext, string Iv, string Tag)? ExtractParts(string message)
    {
        var tagStart = message.Length - encodedAuthTagLength;
        var ivStart = tagStart - 2 - encodedIvLength;
        var ciphertextEnd = ivStart - 2;
        if (ciphertextEnd < 0
            || string.CompareOrdinal(message, tagStart - 2, "--", 0, 2) != 0
            || string.CompareOrdinal(message, ciphertextEnd, "--", 0, 2) != 0)
        {
            return null;
        }
        return (message[..ciphertextEnd], message[ivStart..(tagStart - 2)], message[tagStart..]);
    }
}
