using System.Security.Cryptography;
using System.Text;

namespace Campfire.RailsCompat.Crypto;

/// <summary><c>ActiveSupport::SecurityUtils</c>.</summary>
public static class SecurityUtils
{
    /// <summary>
    /// <c>secure_compare</c>: the lengths may leak, the contents don't. Compares the UTF-8 bytes,
    /// as Ruby compares <c>bytesize</c> and bytes.
    /// </summary>
    public static bool SecureCompare(string a, string b) => SecureCompare(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static bool SecureCompare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}
