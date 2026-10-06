using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>WebPush::Encryption.encrypt</c> (web-push 3.1.0 lib/web_push/encryption.rb): RFC 8291
/// message encryption in the RFC 8188 <c>aes128gcm</c> content coding, framed as the gem frames
/// it: one record whose size field is the ciphertext's length, and the plaintext followed by the
/// last-record delimiter <c>0x02</c> and one byte of zero padding.
/// </summary>
public static class WebPushEncryption
{
    /// <summary>The <c>rs</c> limit: <c>raise ArgumentError, "encrypted payload is too big" if rs > 4096</c>.</summary>
    public const int MaxRecordSize = 4096;

    static readonly byte[] GemPadding = [0x02, 0x00];

    /// <summary>
    /// Encrypts <paramref name="message"/> for a subscription's <c>p256dh</c> key and <c>auth</c>
    /// secret (both urlsafe Base64), with a fresh server key and salt.
    /// </summary>
    /// <exception cref="WebPushArgumentException">A blank argument, bad Base64, or a payload over 4096 bytes.</exception>
    /// <exception cref="WebPushOpenSslException">The p256dh key isn't a P-256 point.</exception>
    public static byte[] Encrypt(string? message, string? p256dh, string? auth)
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(message, p256dh, auth, server, RandomNumberGenerator.GetBytes(16), recordSize: null, GemPadding);
    }

    /// <summary>
    /// The encryption with the server key, salt and framing given: <paramref name="recordSize"/>
    /// null writes the ciphertext's length as the gem does. RFC 8291's example uses 4096 and a
    /// bare delimiter.
    /// </summary>
    internal static byte[] Encrypt(string? message, string? p256dh, string? auth, ECDiffieHellman server, byte[] salt, uint? recordSize, byte[] padding) =>
        Encrypt(message is null ? null : Encoding.UTF8.GetBytes(message), p256dh, auth, server, salt, recordSize, padding);

    internal static byte[] Encrypt(byte[]? message, string? p256dh, string? auth, ECDiffieHellman server, byte[] salt, uint? recordSize, byte[] padding)
    {
        // assert_arguments
        if (message is null || message.Length == 0)
        {
            throw new WebPushArgumentException("message cannot be blank");
        }
        if (string.IsNullOrEmpty(p256dh))
        {
            throw new WebPushArgumentException("p256dh cannot be blank");
        }
        if (string.IsNullOrEmpty(auth))
        {
            throw new WebPushArgumentException("auth cannot be blank");
        }

        var serverPublic = P256.Uncompressed(server.ExportParameters(includePrivateParameters: false).Q);
        var clientPublicBytes = P256.StripLeadingZeros(Decode64(p256dh));
        var clientPoint = P256.DecodePoint(clientPublicBytes);
        var sharedSecret = SharedSecret(server, clientPoint);
        var clientAuthToken = Decode64(auth);

        byte[] info = [.. "WebPush: info\0"u8, .. clientPublicBytes, .. serverPublic];
        var prk = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, clientAuthToken, info);
        var contentEncryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var ciphertext = EncryptPayload([.. message, .. padding], contentEncryptionKey, nonce);
        if (ciphertext.Length > MaxRecordSize)
        {
            throw new WebPushArgumentException("encrypted payload is too big");
        }

        var header = new byte[16 + 4 + 1 + serverPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), recordSize ?? (uint)ciphertext.Length);
        header[20] = (byte)serverPublic.Length;
        serverPublic.CopyTo(header, 21);
        return [.. header, .. ciphertext];
    }

    /// <summary><c>WebPush.decode64</c>: <c>Base64.urlsafe_decode64</c>, which raises ArgumentError.</summary>
    internal static byte[] Decode64(string value) =>
        RubyBase64.UrlSafeDecode(value) ?? throw new WebPushArgumentException("invalid base64");

    // `server.dh_compute_key(client_public_key)`: the shared point's x coordinate.
    static byte[] SharedSecret(ECDiffieHellman server, ECPoint clientPoint)
    {
        try
        {
            using var client = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = clientPoint });
            return server.DeriveRawSecretAgreement(client.PublicKey);
        }
        catch (CryptographicException e)
        {
            throw new WebPushOpenSslException("OpenSSL::PKey::ECError", e.Message, e);
        }
    }

    // `encrypt_payload`: AES-128-GCM, the 16-byte tag appended.
    static byte[] EncryptPayload(byte[] plaintext, byte[] key, byte[] nonce)
    {
        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        var output = new byte[plaintext.Length + AesGcm.TagByteSizes.MaxSize];
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length));
        return output;
    }
}
