using Campfire.Vectors;
using Campfire.Web.QrCode;

namespace Campfire.Web.Tests.QrCode;

// Expected values are rqrcode 3.2.0's own output: vectors/rqrcode.json, from
// reference-tools/campfire/rqrcode.rb.
public sealed class QrCodeSvgTests
{
    [Theory]
    [MemberData(nameof(QrCodeVectors.Cases), MemberType = typeof(QrCodeVectors))]
    public void Version_matches_rqrcode(QrCodeCase vector)
    {
        Assert.Equal(vector.Version, QrCodeMatrix.MinimumVersion(new QrSegment(Convert.FromBase64String(vector.InputBase64))));
    }

    [Theory]
    [MemberData(nameof(QrCodeVectors.Cases), MemberType = typeof(QrCodeVectors))]
    public void Modules_match_rqrcode(QrCodeCase vector)
    {
        var code = QrCodeMatrix.Create(Convert.FromBase64String(vector.InputBase64));

        Assert.NotNull(code);
        Assert.Equal(vector.Version, code.Version);
        var rows = code.Modules.Select(row => string.Concat(row.Select(dark => dark ? '1' : '0')));
        Assert.Equal(vector.Modules, string.Join('\n', rows));
    }

    // rqrcode.rb records the full SVG for the first four inputs and for the controller's own
    // Base64.urlsafe_decode64 of a transfer URL; the modules above cover the rest.
    public static TheoryData<QrCodeCase> SvgCases() => new(QrCodeVectors.File.Where(vector => vector.Svg is not null));

    [Theory]
    [MemberData(nameof(SvgCases))]
    public void Svg_bytes_match_rqrcode(QrCodeCase vector)
    {
        Assert.Equal(vector.Svg, QrCodeSvg.Render(Convert.FromBase64String(vector.InputBase64)));
    }

    [Fact]
    public void Every_vector_with_an_svg_is_checked()
    {
        Assert.Equal(5, SvgCases().Count);
    }

    [Fact]
    public void Data_too_long_for_version_40_renders_nothing()
    {
        Assert.Null(QrCodeSvg.Render(Enumerable.Repeat((byte)'a', 3_000).ToArray()));
        Assert.NotNull(QrCodeSvg.Render("http://campfire.test"u8));
    }

    [Fact]
    public void The_largest_byte_mode_input_fits_version_40()
    {
        // QRMAXBITS[:h][39] is 10208: 4 + 16 + 8n < 10208 holds up to n = 1273.
        Assert.Equal(40, QrCodeMatrix.Create(Enumerable.Repeat((byte)'a', 1_273).ToArray())?.Version);
        Assert.Null(QrCodeMatrix.Create(Enumerable.Repeat((byte)'a', 1_274).ToArray()));
    }
}
