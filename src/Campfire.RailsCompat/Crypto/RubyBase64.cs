namespace Campfire.RailsCompat.Crypto;

/// <summary>Ruby's <c>Base64</c> flavors as Rails uses them.</summary>
public static class RubyBase64
{
    /// <summary><c>Base64.strict_encode64</c>.</summary>
    public static string StrictEncode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data);

    /// <summary><c>Base64.urlsafe_encode64(data, padding:)</c>.</summary>
    public static string UrlSafeEncode(ReadOnlySpan<byte> data, bool padding)
    {
        var encoded = Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');
        return padding ? encoded : encoded.TrimEnd('=');
    }

    /// <summary>
    /// <c>Base64.strict_decode64</c>: standard alphabet, length a multiple of 4, padding only at
    /// the end, no whitespace, and no stray bits in the last character. <c>null</c> where Ruby
    /// raises <c>ArgumentError</c>.
    /// </summary>
    public static byte[]? StrictDecode(string encoded)
    {
        if (encoded.Length % 4 != 0)
        {
            return null;
        }
        var padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
        var body = encoded.AsSpan(0, encoded.Length - padding);
        foreach (var c in body)
        {
            if (Value(c) < 0)
            {
                return null;
            }
        }
        // The bits of the last character that don't make a whole byte must be zero.
        if (padding == 1 && (Value(body[^1]) & 0b11) != 0 || padding == 2 && (Value(body[^1]) & 0b1111) != 0)
        {
            return null;
        }
        return Convert.FromBase64String(encoded);
    }

    /// <summary>
    /// <c>Base64.urlsafe_decode64</c>, which pads a short unpadded string and translates <c>-_</c>
    /// to <c>+/</c> before a strict decode. So it takes either alphabet, even mixed, with or
    /// without padding, but refuses partial padding (<c>"ab="</c>).
    /// </summary>
    public static byte[]? UrlSafeDecode(string encoded)
    {
        var translated = encoded.Replace('-', '+').Replace('_', '/');
        if (!encoded.EndsWith('=') && encoded.Length % 4 != 0)
        {
            translated = translated.PadRight(encoded.Length + (4 - encoded.Length % 4), '=');
        }
        return StrictDecode(translated);
    }

    static int Value(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };
}
