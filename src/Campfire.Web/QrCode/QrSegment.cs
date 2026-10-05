namespace Campfire.Web.QrCode;

enum QrMode
{
    Number = 1,
    AlphaNumeric = 2,
    Byte = 4,
}

// RQRCodeCore::QRSegment: the whole input in one mode. Without a mode the gem picks numeric, then
// alphanumeric, then 8-bit byte. It checks `data.chars`; every numeric and alphanumeric character is
// ASCII, so checking bytes is the same.
sealed class QrSegment
{
    const string alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    readonly byte[] data;

    public QrSegment(ReadOnlySpan<byte> data)
    {
        this.data = data.ToArray();
        Mode = IsAll(data, b => b is >= (byte)'0' and <= (byte)'9') ? QrMode.Number
            : IsAll(data, b => alphanumeric.Contains((char)b, StringComparison.Ordinal)) ? QrMode.AlphaNumeric
            : QrMode.Byte;
    }

    public QrMode Mode { get; }

    // QRSegment#size(version): the bits needed, including the mode indicator and the length.
    public int Size(int version) => 4 + LengthInBits(Mode, version) + ContentSize();

    public void Write(QrBitBuffer buffer)
    {
        buffer.Put((int)Mode, 4);
        buffer.Put(data.Length, LengthInBits(Mode, buffer.Version));
        switch (Mode)
        {
            case QrMode.Number:
                foreach (var chunk in data.Chunk(3))
                {
                    var code = chunk.Aggregate(0, (sum, digit) => sum * 10 + digit - '0');
                    buffer.Put(code, chunk.Length switch { 1 => 4, 2 => 7, _ => 10 });
                }

                break;
            case QrMode.AlphaNumeric:
                foreach (var pair in data.Chunk(2))
                {
                    if (pair.Length == 2)
                    {
                        buffer.Put(Index(pair[0]) * 45 + Index(pair[1]), 11);
                    }
                    else
                    {
                        buffer.Put(Index(pair[0]), 6);
                    }
                }

                break;
            default:
                foreach (var b in data)
                {
                    buffer.Put(b, 8);
                }

                break;
        }
    }

    int ContentSize()
    {
        var length = data.Length;
        var (chunk, bits, extra) = Mode switch
        {
            QrMode.Number => (3, 10, length % 3 == 1 ? 4 : 7),
            QrMode.AlphaNumeric => (2, 11, 6),
            _ => (1, 8, 0),
        };
        return length / chunk * bits + (length % chunk == 0 ? 0 : extra);
    }

    // QRUtil.get_length_in_bits
    static int LengthInBits(QrMode mode, int version)
    {
        var macroVersion = version switch { <= 9 => 0, <= 26 => 1, _ => 2 };
        return mode switch
        {
            QrMode.Number => new[] { 10, 12, 14 }[macroVersion],
            QrMode.AlphaNumeric => new[] { 9, 11, 13 }[macroVersion],
            _ => new[] { 8, 16, 16 }[macroVersion],
        };
    }

    static int Index(byte b) => alphanumeric.IndexOf((char)b, StringComparison.Ordinal);

    static bool IsAll(ReadOnlySpan<byte> data, Func<byte, bool> predicate)
    {
        foreach (var b in data)
        {
            if (!predicate(b))
            {
                return false;
            }
        }

        return true;
    }
}

// RQRCodeCore::QRBitBuffer
sealed class QrBitBuffer(int version)
{
    readonly List<byte> buffer = [];

    public int Version { get; } = version;

    public int Length { get; private set; }

    public IReadOnlyList<byte> Bytes => buffer;

    public void Put(int num, int length)
    {
        for (var i = 0; i < length; i++)
        {
            PutBit(((num >> (length - i - 1)) & 1) == 1);
        }
    }

    public void EndOfMessage(int maxDataBits)
    {
        if (Length + 4 <= maxDataBits)
        {
            Put(0, 4);
        }
    }

    public void PadUntil(int preferredSize)
    {
        while (Length % 8 != 0)
        {
            PutBit(false);
        }

        while (Length < preferredSize)
        {
            Put(0xEC, 8);
            if (Length < preferredSize)
            {
                Put(0x11, 8);
            }
        }
    }

    void PutBit(bool bit)
    {
        var index = Length / 8;
        if (buffer.Count <= index)
        {
            buffer.Add(0);
        }

        if (bit)
        {
            buffer[index] |= (byte)(0x80 >> (Length % 8));
        }

        Length++;
    }
}
