using System.Globalization;
using Campfire.RailsCompat.Ruby;

namespace Campfire.RailsCompat.Formatting;

/// <summary>
/// How Active Record keeps a <c>datetime(6)</c> column on SQLite: UTC text, written by
/// <c>Quoting#quoted_date</c> and read back by <c>ActiveModel::Type::DateTime</c>.
/// </summary>
public static class ActiveRecordTime
{
    static readonly long UnixEpochTicks = DateTimeOffset.UnixEpoch.UtcTicks;

    /// <summary>
    /// The text Active Record writes: <c>"%Y-%m-%d %H:%M:%S"</c> in UTC, then <c>".%06d"</c>
    /// microseconds only when there are any. Anything finer is cut off first, not rounded
    /// (<c>TimeValue#apply_seconds_precision</c> with precision 6).
    /// </summary>
    public static string ToDb(DateTimeOffset time)
    {
        var utc = TruncateToMicroseconds(time).UtcDateTime;
        var seconds = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var microseconds = utc.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMicrosecond;
        return microseconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"{seconds}.{microseconds:D6}") : seconds;
    }

    /// <summary>
    /// What Active Record reads from a datetime column's text, or null where it reads nil. That's
    /// <c>Time.new(text, in: "UTC")</c> (<c>fast_string_to_time</c>) for anything with a <c>-</c> in it,
    /// which covers what Rails writes, SQLite's <c>STRFTIME</c> and <c>CURRENT_TIMESTAMP</c>. Where that
    /// raises Rails tries <c>Date._parse</c>, which isn't ported: no writer produces those spellings.
    /// Ruby keeps nanoseconds; .NET keeps the 100ns ticks below them, and only years 1 to 9999.
    /// </summary>
    public static DateTimeOffset? FromDb(string text)
    {
        if (!text.Contains('-', StringComparison.Ordinal) || RubyTime.Parse(text) is not { } nanoseconds)
        {
            return null;
        }
        var ticks = UnixEpochTicks + FloorDivide(nanoseconds, 100);
        return ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks
            ? null
            : new DateTimeOffset((long)ticks, TimeSpan.Zero);
    }

    /// <summary>A time as an attribute holds it once assigned: whole microseconds.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }

    static Int128 FloorDivide(Int128 value, Int128 divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}
