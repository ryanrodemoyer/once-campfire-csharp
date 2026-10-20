using System.Security.Cryptography;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Server.Cli;

/// <summary>
/// <c>campfire generate-secrets</c> is <c>reference/script/admin/generate-secrets</c>, which the
/// self-hosting guide (reference/docs/self-hosting.md) has people run once to fill in their
/// environment: a <c>SECRET_KEY_BASE</c> and a VAPID key pair for Web Push.
/// </summary>
public static class GenerateSecrets
{
    /// <summary>
    /// The script's three lines: <c>SecureRandom.hex(64)</c>, then <c>WebPush.generate_key</c>'s
    /// <c>private_key</c> and <c>public_key</c>.
    /// </summary>
    public static IEnumerable<string> Lines()
    {
        var (privateKey, publicKey) = VapidKeyPair();
        yield return $"SECRET_KEY_BASE={Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64))}";
        yield return $"VAPID_PRIVATE_KEY={privateKey}";
        yield return $"VAPID_PUBLIC_KEY={publicKey}";
    }

    /// <summary>
    /// <c>WebPush::VapidKey.new</c> (web-push 3.1.0, lib/web_push/vapid_key.rb): a fresh prime256v1
    /// key, its private key as <c>encode64(curve.private_key.to_s(2))</c> and its public key as
    /// <c>encode64(curve.public_key.to_bn.to_s(2))</c>. <c>BN#to_s(2)</c> drops the scalar's leading
    /// zero bytes; the uncompressed point starts with 0x04, so it keeps all 65. <c>WebPush.encode64</c>
    /// is <c>Base64.urlsafe_encode64</c>, which pads.
    /// </summary>
    public static (string PrivateKey, string PublicKey) VapidKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        var scalar = parameters.D!.AsSpan().TrimStart((byte)0);
        byte[] point = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
        return (RubyBase64.UrlSafeEncode(scalar, padding: true), RubyBase64.UrlSafeEncode(point, padding: true));
    }
}
