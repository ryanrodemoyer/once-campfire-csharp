using System.Globalization;

namespace Campfire.RailsCompat.Formatting;

/// <summary>
/// <c>Time::DATE_FORMATS</c> and the other ways the app prints an <c>ActiveSupport::TimeWithZone</c>.
/// The app's zone is UTC (no <c>config.time_zone</c>), so every time is printed in UTC.
/// </summary>
public static class TimeFormats
{
    static readonly long UnixEpochTicks = DateTimeOffset.UnixEpoch.UtcTicks;

    /// <summary><c>to_fs(:number)</c>: <c>"%Y%m%d%H%M%S"</c>, the <c>v</c> of avatar and logo URLs.</summary>
    public static string ToFsNumber(DateTimeOffset time) => Format(time, "yyyyMMddHHmmss");

    /// <summary><c>to_fs(:db)</c>: <c>"%Y-%m-%d %H:%M:%S"</c>.</summary>
    public static string ToFsDb(DateTimeOffset time) => Format(time, "yyyy-MM-dd HH:mm:ss");

    /// <summary><c>to_fs(:usec)</c>: <c>"%Y%m%d%H%M%S%6N"</c>, a record's <c>cache_version</c>.</summary>
    public static string ToFsUsec(DateTimeOffset time) => Format(time, "yyyyMMddHHmmssffffff");

    /// <summary>
    /// <c>to_fs(:epoch)</c>, which <c>reference/config/initializers/time_formats.rb</c> defines as
    /// <c>(time.to_f * 1000).to_i</c> to match JavaScript's <c>getTime()</c>. The float is deliberate:
    /// it takes some milliseconds down by one, and the client compares these numbers.
    /// </summary>
    public static long ToFsEpoch(DateTimeOffset time) => (long)(ToF(time) * 1000);

    /// <summary>
    /// <c>Time#to_f</c> (<c>rb_time_unmagnify_to_float</c> in Ruby 3.4's time.c): whole seconds exactly,
    /// otherwise the nanoseconds since the epoch as a double, divided by 1e9. That's not always the
    /// double nearest the exact time: 2028-05-20 09:54:34.005 is 1842429274.0049999.
    /// </summary>
    public static double ToF(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - UnixEpochTicks;
        if (ticks % TimeSpan.TicksPerSecond == 0)
        {
            return ticks / TimeSpan.TicksPerSecond;
        }
        return (double)((Int128)ticks * 100) / 1e9;
    }

    /// <summary>
    /// <c>iso8601(fraction_digits)</c> (<c>xmlschema</c>): <c>2026-01-01T12:00:00Z</c>, as
    /// <c>local_datetime_tag</c> puts it in <c>datetime</c> (<c>reference/app/helpers/time_helper.rb</c>), with that many
    /// digits of the second, cut off, after a dot when there are any.
    /// </summary>
    public static string Iso8601(DateTimeOffset time, int fractionDigits = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fractionDigits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fractionDigits, 7);
        var fraction = fractionDigits == 0 ? "" : "." + new string('f', fractionDigits);
        return Format(time, $"yyyy-MM-dd'T'HH:mm:ss{fraction}'Z'");
    }

    /// <summary>
    /// <c>as_json</c> with Active Support's default <c>time_precision</c> of 3:
    /// <c>2026-09-26T12:26:46.848Z</c>.
    /// </summary>
    public static string AsJson(DateTimeOffset time) => Iso8601(time, 3);

    static string Format(DateTimeOffset time, string format) => time.UtcDateTime.ToString(format, CultureInfo.InvariantCulture);
}
