namespace Campfire.Storage.Variants;

/// <summary>
/// The part of Ruby's <c>Marshal.dump</c> (format 4.8, marshal.c) a transformations hash needs:
/// <c>ActiveStorage::Variation#digest</c> is <c>SHA1.base64digest(Marshal.dump(transformations))</c>.
/// Repeated symbols become symlinks (<c>;</c>). No value here is shared by reference, so there are no
/// object links.
/// </summary>
static class RubyMarshalWriter
{
    public static byte[] Dump(Transformations transformations)
    {
        using var writer = new Writer();
        writer.Value(transformations);
        return writer.ToArray();
    }

    sealed class Writer : IDisposable
    {
        readonly MemoryStream output = new();
        readonly List<string> symbols = [];

        public Writer()
        {
            output.WriteByte(4);
            output.WriteByte(8);
        }

        public byte[] ToArray() => output.ToArray();

        public void Dispose() => output.Dispose();

        public void Value(object? value)
        {
            switch (value)
            {
                case null:
                    output.WriteByte((byte)'0');
                    break;
                case bool b:
                    output.WriteByte(b ? (byte)'T' : (byte)'F');
                    break;
                case long n:
                    Integer(n);
                    break;
                case RubySymbol symbol:
                    Symbol(symbol.Name);
                    break;
                case string s:
                    // A UTF-8 String carries its encoding as one instance variable, E: true.
                    output.WriteByte((byte)'I');
                    output.WriteByte((byte)'"');
                    Bytes(System.Text.Encoding.UTF8.GetBytes(s));
                    Long(1);
                    Symbol("E");
                    output.WriteByte((byte)'T');
                    break;
                case object?[] items:
                    output.WriteByte((byte)'[');
                    Long(items.Length);
                    foreach (var item in items)
                    {
                        Value(item);
                    }
                    break;
                case Transformations hash:
                    output.WriteByte((byte)'{');
                    Long(hash.Count);
                    foreach (var (key, item) in hash.Entries)
                    {
                        Symbol(key);
                        Value(item);
                    }
                    break;
                default:
                    throw new ArgumentException($"Can't marshal a {value.GetType().Name}", nameof(value));
            }
        }

        // A Fixnum is written inline when its tagged VALUE (2n + 1) fits in 32 bits; otherwise as a Bignum.
        void Integer(long n)
        {
            if (n >= -(1L << 30) && n < 1L << 30)
            {
                output.WriteByte((byte)'i');
                Long(n);
                return;
            }
            output.WriteByte((byte)'l');
            output.WriteByte(n < 0 ? (byte)'-' : (byte)'+');
            var magnitude = n < 0 ? (ulong)-(n + 1) + 1 : (ulong)n;
            var digits = new List<byte>();
            while (magnitude > 0)
            {
                digits.Add((byte)magnitude);
                magnitude >>= 8;
            }
            if (digits.Count % 2 == 1)
            {
                digits.Add(0);
            }
            Long(digits.Count / 2);
            output.Write(digits.ToArray());
        }

        void Symbol(string name)
        {
            var index = symbols.IndexOf(name);
            if (index >= 0)
            {
                output.WriteByte((byte)';');
                Long(index);
                return;
            }
            symbols.Add(name);
            output.WriteByte((byte)':');
            Bytes(System.Text.Encoding.UTF8.GetBytes(name));
        }

        void Bytes(byte[] bytes)
        {
            Long(bytes.Length);
            output.Write(bytes);
        }

        // w_long.
        void Long(long n)
        {
            if (n == 0)
            {
                output.WriteByte(0);
            }
            else if (n is > 0 and < 123)
            {
                output.WriteByte((byte)(n + 5));
            }
            else if (n is > -124 and < 0)
            {
                output.WriteByte((byte)(n - 5));
            }
            else
            {
                Span<byte> buffer = stackalloc byte[9];
                var x = n;
                for (var i = 1; i < buffer.Length; i++)
                {
                    buffer[i] = (byte)x;
                    x >>= 8;
                    if (x == 0 || x == -1)
                    {
                        buffer[0] = (byte)(x == 0 ? i : -i);
                        output.Write(buffer[..(i + 1)]);
                        return;
                    }
                }
            }
        }
    }
}
