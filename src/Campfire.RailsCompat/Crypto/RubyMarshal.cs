namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// The sliver of Ruby's Marshal format needed to read Rails 7-era signed messages, whose payload
/// is a marshaled String (<c>Marshal.dump("gid://campfire/User/1")</c>). Anything else is refused.
/// </summary>
static class RubyMarshal
{
    /// <summary>Marshal format 4.8.</summary>
    public static ReadOnlySpan<byte> Signature => [0x04, 0x08];

    /// <summary>
    /// The bytes of a marshaled String, <c>"\x04\x08" ["I"] '"' &lt;len&gt; &lt;bytes&gt; [&lt;ivars&gt;]</c>,
    /// or <c>null</c>. The encoding ivars are ignored.
    /// </summary>
    public static byte[]? LoadString(ReadOnlySpan<byte> dumped)
    {
        if (!dumped.StartsWith(Signature))
        {
            return null;
        }
        var rest = dumped[Signature.Length..];
        if (rest.StartsWith("I"u8))
        {
            rest = rest[1..];
        }
        if (!rest.StartsWith("\""u8))
        {
            return null;
        }
        rest = rest[1..];
        if (!TryReadFixnum(ref rest, out var length) || length < 0 || length > rest.Length)
        {
            return null;
        }
        return rest[..(int)length].ToArray();
    }

    /// <summary><c>r_long</c> in Ruby's <c>marshal.c</c>.</summary>
    static bool TryReadFixnum(ref ReadOnlySpan<byte> bytes, out long value)
    {
        value = 0;
        if (bytes.IsEmpty)
        {
            return false;
        }
        var first = (sbyte)bytes[0];
        bytes = bytes[1..];
        switch (first)
        {
            case 0:
                return true;
            case >= 1 and <= 4:
                if (bytes.Length < first)
                {
                    return false;
                }
                for (var i = first - 1; i >= 0; i--)
                {
                    value = (value << 8) | bytes[i];
                }
                bytes = bytes[first..];
                return true;
            case >= -4 and <= -1:
                var count = -first;
                if (bytes.Length < count)
                {
                    return false;
                }
                value = -1;
                for (var i = 0; i < count; i++)
                {
                    value &= ~(0xffL << (8 * i));
                    value |= (long)bytes[i] << (8 * i);
                }
                bytes = bytes[count..];
                return true;
            case > 4:
                value = first - 5;
                return true;
            default:
                value = first + 5;
                return true;
        }
    }
}
