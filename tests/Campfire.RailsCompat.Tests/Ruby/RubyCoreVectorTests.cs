using System.Globalization;
using System.Numerics;
using Campfire.RailsCompat.Ruby;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Ruby;

// vectors/ruby_core.json is Ruby 3.4.10, Rack 3.2.6 and Active Record's own output for every
// character from U+0000 to U+00FF and the parsers' edge cases (reference-tools/ruby_core.rb).
public sealed class RubyCoreVectorTests
{
    [Theory]
    [MemberData(nameof(RubyCoreVectors.Strings), MemberType = typeof(RubyCoreVectors))]
    public void ToIMatchesRuby(RubyStringCase vector)
    {
        var expected = BigInteger.Parse(vector.ToI, CultureInfo.InvariantCulture);
        if (expected >= long.MinValue && expected <= long.MaxValue)
        {
            Assert.Equal((long)expected, RubyString.ToIChecked(vector.Input));
            Assert.Equal((long)expected, RubyString.ToI(vector.Input));
        }
        else
        {
            Assert.Null(RubyString.ToIChecked(vector.Input));
            Assert.Equal(expected.Sign > 0 ? long.MaxValue : long.MinValue, RubyString.ToI(vector.Input));
        }
    }

    [Theory]
    [MemberData(nameof(RubyCoreVectors.Strings), MemberType = typeof(RubyCoreVectors))]
    public void IntegerCastMatchesActiveRecord(RubyStringCase vector)
    {
        // Where Rails raises, the vector names the exception and the cast is null.
        if (vector.IntegerCast == "ActiveModel::RangeError")
        {
            Assert.Null(ActiveModelInteger.Cast(vector.Input));
            return;
        }
        var expected = vector.IntegerCast is null ? (long?)null : long.Parse(vector.IntegerCast, CultureInfo.InvariantCulture);
        Assert.Equal(expected, ActiveModelInteger.Cast(vector.Input));
    }

    [Theory]
    [MemberData(nameof(RubyCoreVectors.Strings), MemberType = typeof(RubyCoreVectors))]
    public void ToFMatchesRuby(RubyStringCase vector) =>
        Assert.Equal(vector.ToF, RubyFloat.ToS(RubyFloat.ToF(vector.Input)));

    [Theory]
    [MemberData(nameof(RubyCoreVectors.Strings), MemberType = typeof(RubyCoreVectors))]
    public void StripMatchesRuby(RubyStringCase vector) =>
        Assert.Equal(vector.Strip, RubyString.Strip(vector.Input));

    [Theory]
    [MemberData(nameof(RubyCoreVectors.Strings), MemberType = typeof(RubyCoreVectors))]
    public void EscapesMatchRubyRackAndAddressable(RubyStringCase vector)
    {
        Assert.Equal(vector.HtmlEscape, RubyEscape.HtmlEscape(vector.Input));
        Assert.Equal(vector.CgiEscape, RubyEscape.CgiEscape(vector.Input));
        Assert.Equal(vector.UrlEncode, RubyEscape.UrlEncode(vector.Input));
        Assert.Equal(vector.AddressableUnreserved, RubyEscape.UrlEncode(vector.Input));
        Assert.Equal(vector.RackEscape, RubyEscape.RackEscape(vector.Input));
    }

    [Theory]
    [MemberData(nameof(RubyCoreVectors.Floats), MemberType = typeof(RubyCoreVectors))]
    public void FloatToSMatchesRuby(RubyFloatCase vector)
    {
        var value = BitConverter.Int64BitsToDouble(long.Parse(vector.Bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Assert.Equal(vector.ToS, RubyFloat.ToS(value));
    }

    [Theory]
    [MemberData(nameof(RubyCoreVectors.ByteRanges), MemberType = typeof(RubyCoreVectors))]
    public void ByteRangesMatchRack(ByteRangeCase vector)
    {
        var ranges = RackByteRanges.Parse(vector.Header, vector.Size);
        if (vector.Ranges is null)
        {
            Assert.Null(ranges);
        }
        else
        {
            Assert.NotNull(ranges);
            Assert.Equal(vector.Ranges.Select(r => (r[0], r[1])), ranges);
        }
    }

    [Fact]
    public void VectorsCameFromTheReferenceRuby()
    {
        Assert.Equal("3.4.10", RubyCoreVectors.File.Versions.Ruby);
        Assert.Equal("3.2.6", RubyCoreVectors.File.Versions.Rack);
    }
}
