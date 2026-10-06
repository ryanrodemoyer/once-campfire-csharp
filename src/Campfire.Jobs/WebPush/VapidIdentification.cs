using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>WebPush::Notification#vapid_identification</c>: the subject and
/// <c>Rails.configuration.x.vapid</c> (reference/config/initializers/vapid.rb: the
/// <c>VAPID_PUBLIC_KEY</c> and <c>VAPID_PRIVATE_KEY</c> environment variables), and the
/// <c>Authorization</c> header <c>WebPush::Request#build_vapid_header</c> makes from them (RFC
/// 8292): <c>vapid t=&lt;ES256 JWT&gt;,k=&lt;public key&gt;</c>.
/// <para>
/// The keys are read the way <c>VapidKey.from_keys</c> reads them, on every request: a missing
/// key is replaced by one from a freshly generated pair, and the public key is sent as given
/// even if it isn't the private key's, so the push service is the one to refuse it.
/// </para>
/// </summary>
public sealed class VapidIdentification(string? publicKey, string? privateKey, string subject = VapidIdentification.Subject)
{
    /// <summary>The <c>subject:</c> <c>WebPush::Notification</c> gives.</summary>
    public const string Subject = "mailto:support@37signals.com";

    /// <summary><c>WebPush::Request#expiration</c>'s default: 12 hours.</summary>
    public static readonly TimeSpan Expiration = TimeSpan.FromHours(12);

    /// <summary><c>Rails.configuration.x.vapid.public_key</c></summary>
    public string? PublicKey { get; } = publicKey;

    /// <summary><c>Rails.configuration.x.vapid.private_key</c></summary>
    public string? PrivateKey { get; } = privateKey;

    /// <summary>
    /// <c>VAPID_PUBLIC_KEY</c> and <c>VAPID_PRIVATE_KEY</c> from the environment. The port has no
    /// Rails credentials file, so there is nothing to fall back to.
    /// </summary>
    public static VapidIdentification FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY"), Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY"));

    /// <summary>
    /// <c>build_vapid_header</c> for the push service at <paramref name="audience"/>
    /// (<c>uri.scheme + "://" + uri.host</c>, the JWT's <c>aud</c>), with its <c>exp</c> twelve
    /// hours after <paramref name="now"/>.
    /// </summary>
    /// <exception cref="WebPushArgumentException">A key isn't Base64.</exception>
    /// <exception cref="WebPushOpenSslException">OpenSSL refuses a key.</exception>
    public string Authorization(string audience, DateTimeOffset now)
    {
        using var generated = PublicKey is null || PrivateKey is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var generatedKey = generated?.ExportParameters(includePrivateParameters: true);
        var publicPoint = PublicKey is null ? generatedKey!.Value.Q : P256.DecodePoint(P256.StripLeadingZeros(WebPushEncryption.Decode64(PublicKey)));
        var privateScalar = PrivateKey is null ? generatedKey!.Value.D! : Scalar(WebPushEncryption.Decode64(PrivateKey));

        var claims = new JsonObject
        {
            ["aud"] = audience,
            ["exp"] = (now + Expiration).ToUnixTimeSeconds(),
            ["sub"] = subject,
        };
        var signingInput = $"{Encode64(Encoding.UTF8.GetBytes(jwtHeader))}.{Encode64(Encoding.UTF8.GetBytes(RailsJson.Generate(claims)))}";
        var signature = Sign(privateScalar, Encoding.ASCII.GetBytes(signingInput));
        return $"vapid t={signingInput}.{Encode64(signature)},k={Encode64(P256.Uncompressed(publicPoint))}";
    }

    // `jwt_header_fields`, as JWT.encode writes them.
    const string jwtHeader = """{"typ":"JWT","alg":"ES256"}""";

    // `OpenSSL::BN.new(decode64(key), 2).to_s(2)`, as the 32-byte scalar it stands for.
    static byte[] Scalar(byte[] bytes)
    {
        var scalar = P256.StripLeadingZeros(bytes);
        if (scalar.Length > P256.FieldSize)
        {
            throw new WebPushOpenSslException("OpenSSL::PKey::ECError", "invalid private key");
        }
        return [.. new byte[P256.FieldSize - scalar.Length], .. scalar];
    }

    // ES256 (RFC 7518 section 3.4): the raw r || s, 32 bytes each.
    static byte[] Sign(byte[] privateScalar, byte[] data)
    {
        try
        {
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateScalar });
            return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException e)
        {
            throw new WebPushOpenSslException("OpenSSL::PKey::ECError", e.Message, e);
        }
    }

    // `trim_encode64`, and the JWT gem's Base64url: urlsafe Base64 without padding.
    static string Encode64(byte[] bytes) => RubyBase64.UrlSafeEncode(bytes, padding: false);
}
