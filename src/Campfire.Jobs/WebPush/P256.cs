using System.Numerics;
using System.Security.Cryptography;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// The P-256 points the gem hands to OpenSSL: <c>OpenSSL::PKey::EC::Point.new(group, bn)</c> takes
/// the octet string of <c>OpenSSL::BN.new(bytes, 2)</c>, which drops leading zero bytes, and
/// accepts the uncompressed, compressed and hybrid encodings of a point on the curve.
/// </summary>
static class P256
{
    public const int FieldSize = 32;

    static readonly BigInteger P = Parse("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
    static readonly BigInteger B = Parse("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");

    /// <summary>The octet string <c>BN#to_s(2)</c> gives back: the bytes without leading zeros.</summary>
    public static byte[] StripLeadingZeros(byte[] bytes)
    {
        var zeros = 0;
        while (zeros < bytes.Length && bytes[zeros] == 0)
        {
            zeros++;
        }
        return bytes[zeros..];
    }

    /// <summary>
    /// <c>Point.new(group, bn)</c>: the point's affine coordinates, or
    /// <c>OpenSSL::PKey::EC::Point::Error</c> for anything that isn't a point on P-256.
    /// </summary>
    public static ECPoint DecodePoint(byte[] octets)
    {
        if (octets.Length == 2 * FieldSize + 1 && octets[0] is 0x04 or 0x06 or 0x07)
        {
            var x = Integer(octets.AsSpan(1, FieldSize));
            var y = Integer(octets.AsSpan(1 + FieldSize, FieldSize));
            if (octets[0] != 0x04 && (y.IsEven ? 0x06 : 0x07) != octets[0])
            {
                throw InvalidEncoding();
            }
            return IsOnCurve(x, y) ? Point(x, y) : throw InvalidEncoding();
        }
        if (octets.Length == FieldSize + 1 && octets[0] is 0x02 or 0x03)
        {
            var x = Integer(octets.AsSpan(1, FieldSize));
            if (x >= P)
            {
                throw InvalidEncoding();
            }
            var right = Mod((x * x * x) - (3 * x) + B);
            var y = BigInteger.ModPow(right, (P + 1) / 4, P);
            if (Mod(y * y) != right)
            {
                throw InvalidEncoding();
            }
            if (y.IsEven != (octets[0] == 0x02))
            {
                y = P - y;
            }
            return Point(x, y);
        }
        throw InvalidEncoding();
    }

    /// <summary><c>point.to_bn.to_s(2)</c> in the default (uncompressed) form.</summary>
    public static byte[] Uncompressed(ECPoint point) => [0x04, .. point.X!, .. point.Y!];

    static bool IsOnCurve(BigInteger x, BigInteger y) =>
        x < P && y < P && Mod(y * y) == Mod((x * x * x) - (3 * x) + B);

    static ECPoint Point(BigInteger x, BigInteger y) => new() { X = Bytes(x), Y = Bytes(y) };

    static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    static BigInteger Integer(ReadOnlySpan<byte> bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    static BigInteger Parse(string hex) => Integer(Convert.FromHexString(hex));

    static byte[] Bytes(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        return bytes.Length == FieldSize ? bytes : [.. new byte[FieldSize - bytes.Length], .. bytes];
    }

    static WebPushOpenSslException InvalidEncoding() => new("OpenSSL::PKey::EC::Point::Error", "invalid encoding");
}
