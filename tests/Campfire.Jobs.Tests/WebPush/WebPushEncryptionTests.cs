using System.Security.Cryptography;
using System.Text;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Jobs.Tests.WebPush;

public sealed class WebPushEncryptionTests
{
    static byte[] B64(string value) => RubyBase64.UrlSafeDecode(value)!;

    static string Encode(byte[] bytes) => RubyBase64.UrlSafeEncode(bytes, padding: false);

    // The gem's framing: the ciphertext's length as the record size, then 0x02 0x00 after the
    // message. 86 = salt (16) + rs (4) + idlen (1) + key (65).
    static (uint, string) GemFraming(byte[] body, string message) => ((uint)(body.Length - 86), Convert.ToHexString([.. Encoding.UTF8.GetBytes(message), 0x02, 0x00]));

    [Fact]
    public void The_reference_receiver_decrypts_what_the_gem_encrypted()
    {
        var body = B64(ReferenceVector.Ciphertext);

        Assert.Equal(GemFraming(body, ReferenceVector.Message), ReferenceVector.Receiver.DecryptToHex(body));
    }

    [Fact]
    public void The_reference_receiver_decrypts_what_the_port_encrypts()
    {
        var body = WebPushEncryption.Encrypt(ReferenceVector.Message, ReferenceVector.P256dh, ReferenceVector.Auth);

        Assert.Equal(GemFraming(body, ReferenceVector.Message), ReferenceVector.Receiver.DecryptToHex(body));
        // Same message, same framing: the same length as the gem's ciphertext.
        Assert.Equal(B64(ReferenceVector.Ciphertext).Length, body.Length);
    }

    [Fact]
    public void Every_encryption_has_a_fresh_server_key_and_salt()
    {
        var first = WebPushEncryption.Encrypt("hi", ReferenceVector.P256dh, ReferenceVector.Auth);
        var second = WebPushEncryption.Encrypt("hi", ReferenceVector.P256dh, ReferenceVector.Auth);

        Assert.NotEqual(first[..16], second[..16]);
        Assert.NotEqual(first[21..86], second[21..86]);
    }

    // RFC 8291 section 5, with its framing: rs 4096 and a bare delimiter.
    [Fact]
    public void Matches_the_RFC_8291_example()
    {
        const string plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
        const string asPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
        const string uaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
        const string uaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
        const string salt = "DGv6ra1nlYgDCS1FRnbzlw";
        const string auth = "BTBZMqHH6r4Tts7J_aSIgg";
        const string message = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

        using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = B64(asPrivate) });
        var body = WebPushEncryption.Encrypt(B64(plaintext), uaPublic, auth, server, B64(salt), recordSize: 4096, padding: [0x02]);

        Assert.Equal(message, Encode(body));
        Assert.Equal((4096u, Convert.ToHexString([.. B64(plaintext), 0x02])), WebPushReceiver.FromPrivateKey(uaPrivate, auth).DecryptToHex(B64(message)));
    }

    // OpenSSL::BN.new(bytes, 2) drops leading zero bytes, and OpenSSL takes a compressed point,
    // which the gem then writes into the key info as it was given.
    [Fact]
    public void Reads_the_p256dh_key_as_OpenSSL_does()
    {
        var receiver = WebPushReceiver.Generate();
        var point = receiver.PublicKey;
        byte[] compressed = [(byte)((point[64] & 1) == 0 ? 0x02 : 0x03), .. point[1..33]];

        var padded = WebPushEncryption.Encrypt("hi", Encode([0x00, 0x00, .. point]), receiver.Auth);
        Assert.Equal(GemFraming(padded, "hi"), receiver.DecryptToHex(padded));

        var fromCompressed = WebPushEncryption.Encrypt("hi", Encode(compressed), receiver.Auth);
        Assert.Equal(GemFraming(fromCompressed, "hi"), receiver.DecryptToHex(fromCompressed, keyInInfo: compressed));
    }

    [Fact]
    public void Raises_what_the_gem_raises()
    {
        var ok = WebPushReceiver.Generate().P256dh;

        Assert.Equal("message cannot be blank", Assert.Throws<WebPushArgumentException>(() => WebPushEncryption.Encrypt("", ok, "YXV0aA")).Message);
        Assert.Equal("p256dh cannot be blank", Assert.Throws<WebPushArgumentException>(() => WebPushEncryption.Encrypt("m", null, "YXV0aA")).Message);
        Assert.Equal("auth cannot be blank", Assert.Throws<WebPushArgumentException>(() => WebPushEncryption.Encrypt("m", ok, "")).Message);
        Assert.Throws<WebPushArgumentException>(() => WebPushEncryption.Encrypt("m", "not base64!", "YXV0aA"));
        Assert.Throws<WebPushArgumentException>(() => WebPushEncryption.Encrypt("m", ok, "not base64!"));
        Assert.Equal("encrypted payload is too big",
            Assert.Throws<WebPushArgumentException>(() => WebPushEncryption.Encrypt(new string('x', 4079), ok, "YXV0aA")).Message);
        Assert.Equal(4096 + 86, WebPushEncryption.Encrypt(new string('x', 4078), ok, "YXV0aA").Length);

        // Not a point on P-256: OpenSSL::PKey::EC::Point::Error, an OpenSSL error.
        var point = B64(ok);
        point[64] ^= 1;
        foreach (var key in new[] { "dGVzdF9rZXk", Encode(point), "AA" })
        {
            Assert.Equal("OpenSSL::PKey::EC::Point::Error",
                Assert.Throws<WebPushOpenSslException>(() => WebPushEncryption.Encrypt("m", key, "YXV0aA")).RubyClass);
        }
    }

    // The fixtures' keys (reference/test/fixtures/push/subscriptions.yml) aren't points either.
    [Fact]
    public void The_fixture_keys_are_not_points()
    {
        var error = Assert.Throws<WebPushOpenSslException>(() =>
            WebPushEncryption.Encrypt("m", "123-RIXcMgkdjhRnFZaYjjGvo00dydRQbCpQTuXFjLaCPSE7ofxi19awgGc3Doqa1RmYQqsbQDfQTifFZgc", "xxx2DtgvmLkevKRwoyahJl0efg"));

        Assert.Equal("OpenSSL::PKey::EC::Point::Error", error.RubyClass);
    }
}
