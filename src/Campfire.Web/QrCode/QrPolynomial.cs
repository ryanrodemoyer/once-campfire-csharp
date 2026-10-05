namespace Campfire.Web.QrCode;

// RQRCodeCore::QRMath: GF(256) exp and log tables.
static class QrMath
{
    static readonly int[] ExpTable = BuildExpTable();
    static readonly int[] LogTable = BuildLogTable();

    public static int Glog(int n)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), $"glog({n})");
        }

        return LogTable[n];
    }

    public static int Gexp(int n)
    {
        while (n < 0)
        {
            n += 255;
        }

        while (n >= 256)
        {
            n -= 255;
        }

        return ExpTable[n];
    }

    static int[] BuildExpTable()
    {
        var exp = new int[256];
        for (var i = 0; i < 8; i++)
        {
            exp[i] = 1 << i;
        }

        for (var i = 8; i < 256; i++)
        {
            exp[i] = exp[i - 4] ^ exp[i - 5] ^ exp[i - 6] ^ exp[i - 8];
        }

        return exp;
    }

    static int[] BuildLogTable()
    {
        var log = new int[256];
        for (var i = 0; i < 255; i++)
        {
            log[ExpTable[i]] = i;
        }

        return log;
    }
}

// RQRCodeCore::QRPolynomial. The gem pads with nils where this pads with zeros; they only differ
// for inputs where the gem raises.
sealed class QrPolynomial
{
    public QrPolynomial(IReadOnlyList<int> num, int shift)
    {
        var offset = 0;
        while (offset < num.Count && num[offset] == 0)
        {
            offset++;
        }

        Coefficients = new int[num.Count - offset + shift];
        for (var i = offset; i < num.Count; i++)
        {
            Coefficients[i - offset] = num[i];
        }
    }

    public int[] Coefficients { get; }

    // QRUtil.get_error_correct_polynomial
    public static QrPolynomial ErrorCorrect(int length)
    {
        var a = new QrPolynomial([1], 0);
        for (var i = 0; i < length; i++)
        {
            a = a.Multiply(new QrPolynomial([1, QrMath.Gexp(i)], 0));
        }

        return a;
    }

    public QrPolynomial Multiply(QrPolynomial other)
    {
        var num = new int[Coefficients.Length + other.Coefficients.Length - 1];
        for (var i = 0; i < Coefficients.Length; i++)
        {
            for (var j = 0; j < other.Coefficients.Length; j++)
            {
                num[i + j] ^= QrMath.Gexp(QrMath.Glog(Coefficients[i]) + QrMath.Glog(other.Coefficients[j]));
            }
        }

        return new QrPolynomial(num, 0);
    }

    public QrPolynomial Mod(QrPolynomial other)
    {
        var current = this;
        while (current.Coefficients.Length >= other.Coefficients.Length)
        {
            var ratio = QrMath.Glog(current.Coefficients[0]) - QrMath.Glog(other.Coefficients[0]);
            var num = (int[])current.Coefficients.Clone();
            for (var i = 0; i < other.Coefficients.Length; i++)
            {
                num[i] ^= QrMath.Gexp(QrMath.Glog(other.Coefficients[i]) + ratio);
            }

            current = new QrPolynomial(num, 0);
        }

        return current;
    }
}
