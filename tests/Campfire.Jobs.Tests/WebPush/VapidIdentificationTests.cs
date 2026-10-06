using System.Security.Cryptography;
using System.Text;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Jobs.Tests.WebPush;

public sealed class VapidIdentificationTests
{
    const string audience = "https://fcm.googleapis.com";

    static readonly VapidIdentification Vapid = new(ReferenceVector.VapidPublicKey, ReferenceVector.VapidPrivateKey);

    static (string Jwt, string K) Split(string authorization)
    {
        Assert.StartsWith("vapid t=", authorization, StringComparison.Ordinal);
        var parts = authorization["vapid t=".Length..].Split(",k=");
        return (parts[0], parts[1]);
    }

    static bool Verifies(string jwt, byte[] publicKey)
    {
        var segments = jwt.Split('.');
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        return key.VerifyData(Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"), RubyBase64.UrlSafeDecode(segments[2])!,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    [Fact]
    public void Writes_the_header_and_claims_the_gem_writes()
    {
        var (jwt, k) = Split(Vapid.Authorization(audience, ReferenceVector.Now));
        var segments = jwt.Split('.');

        Assert.Equal(ReferenceVector.String("jwt_header_segment"), segments[0]);
        Assert.Equal(ReferenceVector.String("jwt_payload_segment"), segments[1]);
        Assert.Equal(ReferenceVector.String("authorization_k"), k);
        Assert.Equal(VapidIdentification.Subject, ReferenceVector.String("vapid_subject"));
    }

    [Fact]
    public void Signs_with_ES256_under_the_VAPID_key()
    {
        var (jwt, k) = Split(Vapid.Authorization(audience, ReferenceVector.Now));

        Assert.Equal(64, RubyBase64.UrlSafeDecode(jwt.Split('.')[2])!.Length);
        Assert.True(Verifies(jwt, RubyBase64.UrlSafeDecode(k)!));
    }

    // VapidKey.from_keys fills a missing key from a freshly generated pair.
    [Fact]
    public void Missing_keys_come_from_a_generated_pair()
    {
        var (jwt, k) = Split(new VapidIdentification(null, null).Authorization(audience, ReferenceVector.Now));
        Assert.True(Verifies(jwt, RubyBase64.UrlSafeDecode(k)!));

        var (_, onlyPrivate) = Split(new VapidIdentification(null, ReferenceVector.VapidPrivateKey).Authorization(audience, ReferenceVector.Now));
        Assert.NotEqual(ReferenceVector.String("authorization_k"), onlyPrivate);
    }

    [Fact]
    public void A_public_key_that_is_not_a_point_is_an_OpenSSL_error()
    {
        var vapid = new VapidIdentification("dGVzdF9rZXk", ReferenceVector.VapidPrivateKey);

        Assert.Equal("OpenSSL::PKey::EC::Point::Error", Assert.Throws<WebPushOpenSslException>(() => vapid.Authorization(audience, ReferenceVector.Now)).RubyClass);
    }
}
