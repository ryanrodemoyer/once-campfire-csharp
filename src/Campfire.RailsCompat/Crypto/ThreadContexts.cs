using System.Security.Cryptography;

namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// One HMAC and one AES-GCM context per thread, kept while the thread keeps using the same key.
/// OpenSSL 3 sets up a context on every one-shot call, which costs about as much as the
/// operation itself on a cookie-sized message. Every request verifies and re-signs the
/// <c>session_token</c> cookie and decrypts and re-encrypts the session cookie, and every
/// request's cookie jar builds its verifier and encryptor from the same keys, so the contexts
/// carry over between requests. The output is the same as the one-shot APIs'.
/// </summary>
static class ThreadContexts
{
    [ThreadStatic] static Slot<IncrementalHash>? hmacSha1;
    [ThreadStatic] static Slot<IncrementalHash>? hmacSha256;
    [ThreadStatic] static Slot<AesGcm>? aesGcm;

    /// <summary>HMAC of <paramref name="data"/> under <paramref name="key"/>, written to <paramref name="mac"/>.</summary>
    public static int Hmac(MessageDigest digest, byte[] key, ReadOnlySpan<byte> data, Span<byte> mac)
    {
        var hmac = digest == MessageDigest.Sha1
            ? For(ref hmacSha1, key, k => IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, k))
            : For(ref hmacSha256, key, k => IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, k));
        hmac.AppendData(data);
        return hmac.GetHashAndReset(mac);
    }

    /// <summary>
    /// This thread's AES-GCM context for <paramref name="key"/>, with a <paramref name="tagLength"/>-byte
    /// tag. Use it before the next call on this thread, and don't dispose it.
    /// </summary>
    public static AesGcm AesGcm(byte[] key, int tagLength) =>
        For(ref aesGcm, key, k => new AesGcm(k, tagLength), tagLength);

    static T For<T>(ref Slot<T>? slot, byte[] key, Func<byte[], T> create, int variant = 0)
        where T : class, IDisposable
    {
        if (slot is { } held && held.Variant == variant && held.Key.AsSpan().SequenceEqual(key))
        {
            return held.Context;
        }
        slot?.Context.Dispose();
        slot = null;
        var copy = (byte[])key.Clone();
        slot = new Slot<T>(copy, variant, create(copy));
        return slot.Context;
    }

    sealed record Slot<T>(byte[] Key, int Variant, T Context);
}
