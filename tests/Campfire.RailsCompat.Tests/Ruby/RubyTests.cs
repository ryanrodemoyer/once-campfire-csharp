using System.Text;
using Campfire.RailsCompat.Ruby;

namespace Campfire.RailsCompat.Tests.Ruby;

// Hand-checked against Ruby 3.4, for what the vectors reach only through other inputs.
public sealed class RubyTests
{
    [Fact]
    public void ToFDropsWhatRubyHasNoRoomFor()
    {
        // Significand characters past 60, when there's more after the number.
        Assert.Equal(1e59, RubyFloat.ToF("1" + new string('0', 70) + "x"));
        Assert.Equal(1e70, RubyFloat.ToF("1" + new string('0', 70)));
        Assert.Equal(-3.0, RubyFloat.ToF("-0x1.8p1"));
        Assert.Equal(1.0, RubyFloat.ToF("1\02"));
    }

    [Fact]
    public void FloatToSBreaksTiesToEven()
    {
        // Of two shortest forms equally close, the even one, as long as it reads back.
        Assert.Equal("667020902720176.2", RubyFloat.ToS(667020902720176.25));
        Assert.Equal("667020902720176.8", RubyFloat.ToS(667020902720176.75));
        Assert.Equal("24603114260468.062", RubyFloat.ToS(24603114260468.0625));
        Assert.Equal("5.960464477539063e-08", RubyFloat.ToS(Math.Pow(2, -24)));
        Assert.Equal("1.0e+15", RubyFloat.ToS(1e15));
        Assert.Equal("1000000000000000.1", RubyFloat.ToS(1000000000000000.1));
    }

    [Fact]
    public void FloatToSReadsBackWhereDotNetsShortestFormDoesNot()
    {
        // double.ToString("R") gives 2.980232238769531E-08 and 4.104536801298376E-289, which read
        // back as the next double down. Every power of two must print as a form that reads back.
        Assert.Equal("2.9802322387695312e-08", RubyFloat.ToS(Math.Pow(2, -25)));
        for (var exponent = -1074; exponent < 1024; exponent++)
        {
            var value = Math.Pow(2, exponent);
            Assert.Equal(value, double.Parse(RubyFloat.ToS(value), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void AppendsHtmlEscapedText()
    {
        var builder = new StringBuilder("a");
        RubyEscape.AppendHtmlEscaped(builder, "<b>'x' & \"y\"");
        Assert.Equal("a&lt;b&gt;&#39;x&#39; &amp; &quot;y&quot;", builder.ToString());
    }

    [Fact]
    public void ByteRangesCountWhatRackCounts()
    {
        // 99 commas are read, and 100 aren't (max_ranges).
        Assert.Empty(RackByteRanges.Parse("bytes=0-1," + string.Concat(Enumerable.Repeat("0-0,", 98)), 10)!);
        Assert.Null(RackByteRanges.Parse("bytes=0-1," + string.Concat(Enumerable.Repeat("0-0,", 99)), 10));
        Assert.Equal([(0L, 9L)], RackByteRanges.Parse("bytes=0-99999999999999999999", 10));
    }

    [Fact]
    public void UtcOffsetsAreRubys()
    {
        Assert.Equal(0, RubyTime.UtcOffset("Z"));
        Assert.Equal(0, RubyTime.UtcOffset("utc"));
        Assert.Equal(3600, RubyTime.UtcOffset("A"));
        Assert.Equal(12 * 3600, RubyTime.UtcOffset("M"));
        Assert.Equal(-12 * 3600, RubyTime.UtcOffset("Y"));
        Assert.Null(RubyTime.UtcOffset("J"));
        Assert.Equal(-(5 * 3600 + 30 * 60), RubyTime.UtcOffset("-05:30"));
        Assert.Equal(3600 + 2 * 60 + 3, RubyTime.UtcOffset("+010203"));
        Assert.Equal(3600 + 2 * 60 + 3, RubyTime.UtcOffset("+01:02:03"));
        Assert.Null(RubyTime.UtcOffset("+01:60"));
        Assert.Null(RubyTime.UtcOffset("+1:00"));
        Assert.Null(RubyTime.UtcOffset("EST"));
    }
}
