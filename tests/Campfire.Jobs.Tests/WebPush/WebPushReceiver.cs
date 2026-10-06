using System.Buffers.Binary;
using System.Security.Cryptography;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Jobs.Tests.WebPush;

/// <summary>
/// What a browser does with a push (RFC 8291 section 3.4, RFC 8188): derives the key from its
/// own private key, the auth secret and the sender's key in the header, and decrypts the record.
/// </summary>
sealed class WebPushReceiver(ECParameters key, byte[] auth)
{
    public byte[] PublicKey => [0x04, .. key.Q.X!, .. key.Q.Y!];

    public string P256dh => RubyBase64.UrlSafeEncode(PublicKey, padding: true);

    public string Auth => RubyBase64.UrlSafeEncode(auth, padding: true);

    public static WebPushReceiver FromPrivateKey(string privateKey, string auth)
    {
        using var ecdh = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = RubyBase64.UrlSafeDecode(privateKey) });
        return new(ecdh.ExportParameters(includePrivateParameters: true), RubyBase64.UrlSafeDecode(auth)!);
    }

    public static WebPushReceiver Generate()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new(ecdh.ExportParameters(includePrivateParameters: true), RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The record size from the header and the plaintext with its padding, in hex.</summary>
    public (uint RecordSize, string Plaintext) DecryptToHex(byte[] body, byte[]? keyInInfo = null)
    {
        var (recordSize, plaintext) = Decrypt(body, keyInInfo);
        return (recordSize, Convert.ToHexString(plaintext));
    }

    /// <summary>
    /// The record size from the header and the plaintext with its padding. The key info carries
    /// <paramref name="keyInInfo"/> as the receiver's key when given: the bytes the sender was
    /// handed, which the gem uses as they are.
    /// </summary>
    public (uint RecordSize, byte[] Plaintext) Decrypt(byte[] body, byte[]? keyInInfo = null)
    {
        var salt = body[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16));
        var idLength = body[20];
        var senderPublic = body[21..(21 + idLength)];
        var ciphertext = body[(21 + idLength)..];

        using var receiver = ECDiffieHellman.Create(key);
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });
        var sharedSecret = receiver.DeriveRawSecretAgreement(sender.PublicKey);
        byte[] info = [.. "WebPush: info\0"u8, .. keyInInfo ?? PublicKey, .. senderPublic];
        var prk = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, auth, info);
        var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var plaintext = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(contentKey, 16);
        aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plaintext);
        return (recordSize, plaintext);
    }
}
