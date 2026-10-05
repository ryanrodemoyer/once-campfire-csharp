using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// <c>Rails.application.key_generator</c>: an <c>ActiveSupport::CachingKeyGenerator</c> over
/// PBKDF2-HMAC. Railties builds it with 1000 iterations, and <c>load_defaults</c> 7.0+ sets its
/// digest to SHA256 (<c>key_generator_hash_digest_class</c>). Keys are 64 bytes unless asked
/// otherwise; encrypted cookies ask for 32.
/// </summary>
public sealed class KeyGenerator(string secretKeyBase)
{
    public const int Iterations = 1000;
    public const int DefaultKeyLength = 64;

    readonly byte[] secret = Encoding.UTF8.GetBytes(secretKeyBase);
    readonly ConcurrentDictionary<(string Salt, int Length), byte[]> cache = new();

    /// <summary><c>generate_key(salt, length)</c>. Each call returns its own copy.</summary>
    public byte[] GenerateKey(string salt, int length = DefaultKeyLength)
    {
        var key = cache.GetOrAdd((salt, length), Derive);
        return (byte[])key.Clone();
    }

    byte[] Derive((string Salt, int Length) request) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, Encoding.UTF8.GetBytes(request.Salt), Iterations, HashAlgorithmName.SHA256, request.Length);
}
