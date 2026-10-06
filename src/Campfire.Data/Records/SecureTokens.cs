using System.Security.Cryptography;

namespace Campfire.Data.Records;

// The random values models generate, in Ruby's alphabets.
public static class SecureTokens
{
    const string alphanumericCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // `SecureRandom::BASE58_ALPHABET`: digits and letters without 0, O, I and l.
    const string base58Characters = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    // `SecureRandom.alphanumeric(length)`
    public static string Alphanumeric(int length) => RandomNumberGenerator.GetString(alphanumericCharacters, length);

    // `SecureRandom.base58(length)`
    public static string Base58(int length) => RandomNumberGenerator.GetString(base58Characters, length);

    // `Random.uuid` / `SecureRandom.uuid`: a lowercase version 4 UUID.
    public static string Uuid() => Guid.NewGuid().ToString("D");
}
