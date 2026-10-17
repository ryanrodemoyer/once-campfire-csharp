using System.Security.Cryptography;
using System.Text;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Tests.Crypto;

public class ThreadContextsTests
{
    static readonly byte[] Data = Encoding.UTF8.GetBytes("eyJfcmFpbHMiOnsiZGF0YSI6InRva2VuIn19");

    [Fact]
    public void Hmac_matches_the_one_shot_digest_as_keys_and_digests_alternate()
    {
        byte[][] keys = [Key(1, 64), Key(2, 64), Key(1, 32)];
        for (var round = 0; round < 3; round++)
        {
            foreach (var key in keys)
            {
#pragma warning disable CA5350 // Rails signs cookies with HMAC-SHA1.
                Assert.Equal(HMACSHA1.HashData(key, Data), Hmac(MessageDigest.Sha1, key));
#pragma warning restore CA5350
                Assert.Equal(HMACSHA256.HashData(key, Data), Hmac(MessageDigest.Sha256, key));
            }
        }
    }

    [Fact]
    public void A_key_changed_in_place_is_not_mistaken_for_the_one_held()
    {
        var key = Key(3, 64);
        var before = Hmac(MessageDigest.Sha1, key);
        key[0] ^= 1;

        Assert.NotEqual(before, Hmac(MessageDigest.Sha1, key));
#pragma warning disable CA5350
        Assert.Equal(HMACSHA1.HashData(key, Data), Hmac(MessageDigest.Sha1, key));
#pragma warning restore CA5350
    }

    [Fact]
    public void Aes_gcm_still_decrypts_after_a_tag_mismatch()
    {
        var encryptor = new MessageEncryptor(Key(4, 32), MessageSerializer.Null);
        var message = encryptor.EncryptAndSignRaw("\"hi\"", "p");
        var tagStart = message.Length - 24;
        var tampered = message[..tagStart] + (message[tagStart] == 'A' ? "B" : "A") + message[(tagStart + 1)..];

        Assert.Null(encryptor.Decrypt(tampered));
        Assert.Equal("\"hi\"", encryptor.DecryptAndVerify(message, "p", DateTimeOffset.UtcNow).Value!.GetValue<string>());
    }

    [Fact]
    public void Encryptors_with_different_keys_on_one_thread_keep_their_own_keys()
    {
        var first = new MessageEncryptor(Key(5, 32), MessageSerializer.Null);
        var second = new MessageEncryptor(Key(6, 32), MessageSerializer.Null);
        var fromFirst = first.EncryptAndSignRaw("\"a\"");
        var fromSecond = second.EncryptAndSignRaw("\"b\"");

        Assert.Null(second.Decrypt(fromFirst));
        Assert.Null(first.Decrypt(fromSecond));
        Assert.NotNull(first.Decrypt(fromFirst));
        Assert.NotNull(second.Decrypt(fromSecond));
    }

    [Fact]
    public void Threads_sharing_a_verifier_and_encryptor_get_correct_results()
    {
        var verifier = new MessageVerifier(Key(7, 64), MessageDigest.Sha1, MessageEncoding.Strict, MessageSerializer.Null);
        var encryptor = new MessageEncryptor(Key(8, 32), MessageSerializer.Null);
        var now = DateTimeOffset.UtcNow;

        Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var value = $"\"value-{i}\"";
            Assert.Equal(value, verifier.VerifyRaw(verifier.GenerateRaw(value, "p"), "p", now).Value);
            Assert.Equal(value, encryptor.DecryptAndVerify(encryptor.EncryptAndSignRaw(value, "p"), "p", now).Value!.GetValue<string>());
        });
    }

    static byte[] Hmac(MessageDigest digest, byte[] key)
    {
        var mac = new byte[HMACSHA256.HashSizeInBytes];
        return mac[..ThreadContexts.Hmac(digest, key, Data, mac)];
    }

    static byte[] Key(byte seed, int length) => Enumerable.Range(0, length).Select(i => (byte)(seed * 31 + i)).ToArray();
}
