namespace Campfire.Web.QrCode;

// RQRCodeCore::QRCode as `RQRCode::QRCode.new(data)` builds it: a single segment, error correction
// level H, the smallest version whose capacity is strictly greater than the segment's bits, and the
// first mask with the fewest lost points as QRUtil.get_lost_points scores them (a Float, because of
// its dark-ratio term). These follow the gem rather than the QR spec's optimal choices.
sealed class QrCodeMatrix
{
    QrCodeMatrix(int version, bool[][] modules)
    {
        Version = version;
        Modules = modules;
    }

    public int Version { get; }

    // Rows of modules, true for dark.
    public bool[][] Modules { get; }

    // Null where rqrcode raises "Data length exceed maximum capacity of version 40".
    public static QrCodeMatrix? Create(ReadOnlySpan<byte> input)
    {
        var segment = new QrSegment(input);
        if (MinimumVersion(segment) is not { } version)
        {
            return null;
        }

        var count = version * 4 + 17;
        var common = new bool?[count, count];
        PlacePositionProbePattern(common, 0, 0);
        PlacePositionProbePattern(common, count - 7, 0);
        PlacePositionProbePattern(common, 0, count - 7);
        PlacePositionAdjustPattern(common, version);
        PlaceTimingPattern(common);

        var data = CreateData(version, segment);

        bool[][] Make(bool test, int pattern)
        {
            var grid = (bool?[,])common.Clone();
            PlaceFormatInfo(grid, test, pattern);
            if (version >= 7)
            {
                PlaceVersionInfo(grid, version, test);
            }

            MapData(grid, data, pattern);
            return Enumerable.Range(0, count)
                .Select(row => Enumerable.Range(0, count).Select(col => grid[row, col] ?? false).ToArray())
                .ToArray();
        }

        // QRCode#get_best_mask_pattern
        var bestPattern = 0;
        var bestPoints = double.MaxValue;
        for (var pattern = 0; pattern < 8; pattern++)
        {
            var points = LostPoints(Make(true, pattern));
            if (pattern == 0 || bestPoints > points)
            {
                bestPattern = pattern;
                bestPoints = points;
            }
        }

        return new QrCodeMatrix(version, Make(false, bestPattern));
    }

    // QRCode#minimum_version
    internal static int? MinimumVersion(QrSegment segment)
    {
        for (var version = 1; version <= 40; version++)
        {
            if (segment.Size(version) < QrTables.MaxBitsH[version - 1])
            {
                return version;
            }
        }

        return null;
    }

    // QRCode#create_data: the data and error correction codewords, interleaved.
    static byte[] CreateData(int version, QrSegment segment)
    {
        var blocks = new List<(int Total, int Data)>();
        var groups = QrTables.RsBlocksH[version - 1];
        for (var g = 0; g < groups.Length; g += 3)
        {
            blocks.AddRange(Enumerable.Repeat((groups[g + 1], groups[g + 2]), groups[g]));
        }

        var maxDataBits = blocks.Sum(block => block.Data) * 8;

        var buffer = new QrBitBuffer(version);
        segment.Write(buffer);
        buffer.EndOfMessage(maxDataBits);
        if (buffer.Length > maxDataBits)
        {
            throw new InvalidOperationException("code length overflow");
        }

        buffer.PadUntil(maxDataBits);

        var offset = 0;
        var dcData = new List<int[]>();
        var ecData = new List<int[]>();
        foreach (var (total, dataCount) in blocks)
        {
            var dc = new int[dataCount];
            for (var i = 0; i < dataCount; i++)
            {
                dc[i] = buffer.Bytes[offset + i];
            }

            offset += dataCount;
            var rsPoly = QrPolynomial.ErrorCorrect(total - dataCount);
            var ecLength = rsPoly.Coefficients.Length - 1;
            var modPoly = new QrPolynomial(dc, ecLength).Mod(rsPoly).Coefficients;
            var ec = new int[ecLength];
            for (var i = 0; i < ecLength; i++)
            {
                var index = i + modPoly.Length - ecLength;
                ec[i] = index >= 0 ? modPoly[index] : 0;
            }

            dcData.Add(dc);
            ecData.Add(ec);
        }

        var codewords = new List<byte>();
        foreach (var blockCodewords in new[] { dcData, ecData })
        {
            var longest = blockCodewords.Max(block => block.Length);
            for (var i = 0; i < longest; i++)
            {
                foreach (var block in blockCodewords.Where(block => i < block.Length))
                {
                    codewords.Add((byte)block[i]);
                }
            }
        }

        return [.. codewords];
    }

    static void PlacePositionProbePattern(bool?[,] grid, int row, int col)
    {
        var count = grid.GetLength(0);
        for (var r = -1; r <= 7; r++)
        {
            var y = row + r;
            if (y < 0 || y >= count)
            {
                continue;
            }

            for (var c = -1; c <= 7; c++)
            {
                var x = col + c;
                if (x < 0 || x >= count)
                {
                    continue;
                }

                var vertical = r is >= 0 and <= 6 && (c == 0 || c == 6);
                var horizontal = c is >= 0 and <= 6 && (r == 0 || r == 6);
                var square = r is >= 2 and <= 4 && c is >= 2 and <= 4;
                grid[y, x] = vertical || horizontal || square;
            }
        }
    }

    static void PlacePositionAdjustPattern(bool?[,] grid, int version)
    {
        var positions = QrTables.PatternPositions[version - 1];
        foreach (var row in positions)
        {
            foreach (var col in positions)
            {
                if (grid[row, col] is not null)
                {
                    continue;
                }

                for (var r = -2; r <= 2; r++)
                {
                    for (var c = -2; c <= 2; c++)
                    {
                        grid[row + r, col + c] = Math.Abs(r) == 2 || Math.Abs(c) == 2 || (r == 0 && c == 0);
                    }
                }
            }
        }
    }

    static void PlaceTimingPattern(bool?[,] grid)
    {
        var count = grid.GetLength(0);
        for (var i = 8; i < count - 8; i++)
        {
            grid[i, 6] = i % 2 == 0;
            grid[6, i] = i % 2 == 0;
        }
    }

    static void PlaceVersionInfo(bool?[,] grid, int version, bool test)
    {
        var count = grid.GetLength(0);
        var bits = QrBch.Version(version);
        for (var i = 0; i < 18; i++)
        {
            var dark = !test && ((bits >> i) & 1) == 1;
            grid[i / 3, i % 3 + count - 8 - 3] = dark;
            grid[i % 3 + count - 8 - 3, i / 3] = dark;
        }
    }

    static void PlaceFormatInfo(bool?[,] grid, bool test, int pattern)
    {
        var count = grid.GetLength(0);
        var bits = QrBch.FormatInfo((QrTables.LevelH << 3) | pattern);
        for (var i = 0; i < 15; i++)
        {
            var dark = !test && ((bits >> i) & 1) == 1;
            var row = i < 6 ? i : i < 8 ? i + 1 : count - 15 + i;
            grid[row, 8] = dark;
            var col = i < 8 ? count - i - 1 : i < 9 ? 15 - i : 15 - i - 1;
            grid[8, col] = dark;
        }

        grid[count - 8, 8] = !test;
    }

    static void MapData(bool?[,] grid, byte[] data, int pattern)
    {
        var count = grid.GetLength(0);
        var inc = -1;
        var row = count - 1;
        var bitIndex = 7;
        var byteIndex = 0;

        for (var col = count - 1; col >= 1; col -= 2)
        {
            var c0 = col <= 6 ? col - 1 : col;
            while (true)
            {
                for (var c = 0; c < 2; c++)
                {
                    var x = c0 - c;
                    if (grid[row, x] is not null)
                    {
                        continue;
                    }

                    var dark = byteIndex < data.Length && ((data[byteIndex] >> bitIndex) & 1) == 1;
                    grid[row, x] = dark ^ Mask(pattern, row, x);
                    bitIndex--;
                    if (bitIndex == -1)
                    {
                        byteIndex++;
                        bitIndex = 7;
                    }
                }

                row += inc;
                if (row < 0 || count <= row)
                {
                    row -= inc;
                    inc = -inc;
                    break;
                }
            }
        }
    }

    // QRMASKCOMPUTATIONS
    static bool Mask(int pattern, int i, int j) => pattern switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => (i * j) % 2 + (i * j) % 3 == 0,
        6 => ((i * j) % 2 + (i * j) % 3) % 2 == 0,
        7 => ((i * j) % 3 + (i + j) % 2) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };

    // QRUtil.get_lost_points. The dark-ratio term is a Float in Ruby, so the total is too.
    static double LostPoints(bool[][] modules)
    {
        var count = modules.Length;
        var max = count - 1;
        var points = 0L;

        // Same-colour neighbours.
        for (var row = 0; row < count; row++)
        {
            for (var col = 0; col < count; col++)
            {
                var dark = modules[row][col];
                var same = 0;
                for (var r = -1; r <= 1; r++)
                {
                    for (var c = -1; c <= 1; c++)
                    {
                        var y = row + r;
                        var x = col + c;
                        if ((r != 0 || c != 0) && y >= 0 && y <= max && x >= 0 && x <= max && modules[y][x] == dark)
                        {
                            same++;
                        }
                    }
                }

                if (same > 5)
                {
                    points += 3 + same - 5;
                }
            }
        }

        // 2x2 blocks.
        for (var row = 0; row < max; row++)
        {
            for (var col = 0; col < max; col++)
            {
                var value = modules[row][col];
                if (value == modules[row + 1][col] && value == modules[row][col + 1] && value == modules[row + 1][col + 1])
                {
                    points += 3;
                }
            }
        }

        // 1:1:3:1:1 patterns, in rows then columns.
        for (var start = 0; start < count - 6; start++)
        {
            for (var line = 0; line < count; line++)
            {
                if (IsFinderLike(k => modules[line][start + k]))
                {
                    points += 40;
                }

                if (IsFinderLike(k => modules[start + k][line]))
                {
                    points += 40;
                }
            }
        }

        // Dark ratio.
        var darkCount = modules.Sum(row => row.Count(dark => dark));
        var ratio = (double)darkCount / (count * count);
        var delta = Math.Abs(100.0 * ratio - 50.0) / 5.0;
        return points + delta * 10.0;
    }

    static bool IsFinderLike(Func<int, bool> cell) =>
        cell(0) && !cell(1) && cell(2) && cell(3) && cell(4) && !cell(5) && cell(6);
}

// QRUtil's BCH codes for the format and version information.
static class QrBch
{
    const int g15 = (1 << 10) | (1 << 8) | (1 << 5) | (1 << 4) | (1 << 2) | (1 << 1) | 1;
    const int g18 = (1 << 12) | (1 << 11) | (1 << 10) | (1 << 9) | (1 << 8) | (1 << 5) | (1 << 2) | 1;
    const int g15Mask = (1 << 14) | (1 << 12) | (1 << 10) | (1 << 4) | (1 << 1);

    // QRUtil.get_bch_format_info
    public static int FormatInfo(int data)
    {
        var d = data << 10;
        while (Digit(d) - Digit(g15) >= 0)
        {
            d ^= g15 << (Digit(d) - Digit(g15));
        }

        return ((data << 10) | d) ^ g15Mask;
    }

    // QRUtil.get_bch_version
    public static int Version(int data)
    {
        var d = data << 12;
        while (Digit(d) - Digit(g18) >= 0)
        {
            d ^= g18 << (Digit(d) - Digit(g18));
        }

        return (data << 12) | d;
    }

    // QRUtil.get_bch_digit
    static int Digit(int data)
    {
        var digit = 0;
        while (data != 0)
        {
            digit++;
            data >>>= 1;
        }

        return digit;
    }
}
