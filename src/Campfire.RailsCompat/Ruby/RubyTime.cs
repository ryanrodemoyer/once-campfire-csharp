namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// <c>Time.new(string, in: zone)</c> (<c>time_init_parse</c> in Ruby 3.4's time.c, with the default
/// <c>precision: 9</c>): <c>[+-]YYYY[-MM[-DD[( |T)HH:MM:SS[.fraction]]]]</c>, then an optional
/// offset after any spaces. Days past a month's end, hour 24 and second 60 roll over, as
/// <c>timegm</c> does.
/// </summary>
public static class RubyTime
{
    static readonly long NanosecondsPerSecond = 1_000_000_000;

    /// <summary>
    /// The instant in nanoseconds since the Unix epoch, or null where Ruby raises.
    /// <paramref name="defaultOffsetSeconds"/> is the <c>in:</c> zone's offset, used when the string
    /// names none.
    /// </summary>
    public static Int128? Parse(string text, int defaultOffsetSeconds = 0)
    {
        var s = text.AsSpan();
        if (s.Length > 0 && (RubyString.IsSpace(s[0]) || RubyString.IsSpace(s[^1])))
        {
            return null;
        }

        var p = 0;
        if (ParseYear(s, ref p) is not { } year)
        {
            return null;
        }
        int mon = -1, mday = -1, hour = -1, min = -1, sec = -1;
        long subsec = 0;
        if (p < s.Length)
        {
            if (!ParseDateAndTime(s, ref p, ref mon, ref mday, ref hour, ref min, ref sec, ref subsec))
            {
                return null;
            }
            while (p < s.Length && RubyString.IsSpace(s[p]))
            {
                p++;
            }
            var zoneStart = p;
            while (p < s.Length && !RubyString.IsSpace(s[p]))
            {
                p++;
            }
            var zone = s[zoneStart..p];
            while (p < s.Length && RubyString.IsSpace(s[p]))
            {
                p++;
            }
            if (p < s.Length)
            {
                return null;
            }
            if (!zone.IsEmpty)
            {
                if (UtcOffset(zone) is not { } offset)
                {
                    return null;
                }
                defaultOffsetSeconds = offset;
            }
            else if (hour == -1)
            {
                return null;
            }
        }

        // Parts left out default to the start of the year.
        (mon, mday, hour, min, sec) = (mon < 0 ? 1 : mon, mday < 0 ? 1 : mday, hour < 0 ? 0 : hour, min < 0 ? 0 : min, sec < 0 ? 0 : sec);
        // validate_vtm
        if (mon is < 1 or > 12 || mday is < 1 or > 31 || hour > 24 || min > (hour == 24 ? 0 : 59) || sec > (hour == 24 ? 0 : 60))
        {
            return null;
        }
        if (defaultOffsetSeconds <= -86400 || defaultOffsetSeconds >= 86400)
        {
            return null;
        }

        var seconds = (Int128)DaysFromCivil(year, mon, 1) * 86400 + (mday - 1) * 86400L + hour * 3600L + min * 60L + sec - defaultOffsetSeconds;
        return seconds * NanosecondsPerSecond + subsec;
    }

    /// <summary>
    /// <c>rb_int_parse_cstr</c> with a sign: four digits or more (leading zeros count). Years past
    /// nine digits are refused rather than carried as a Bignum.
    /// </summary>
    static long? ParseYear(ReadOnlySpan<char> s, ref int p)
    {
        var negative = false;
        if (p < s.Length && s[p] is '+' or '-')
        {
            negative = s[p] == '-';
            p++;
        }
        var start = p;
        while (p < s.Length && char.IsAsciiDigit(s[p]))
        {
            p++;
        }
        var digits = s[start..p].TrimStart('0');
        if (p - start < 4 || digits.Length > 9)
        {
            return null;
        }
        var year = digits.IsEmpty ? 0 : long.Parse(digits, provider: null);
        return negative ? -year : year;
    }

    /// <summary>
    /// Everything after the year up to the zone. Each part is optional, and where one stops early
    /// what's left is read as the zone.
    /// </summary>
    static bool ParseDateAndTime(ReadOnlySpan<char> s, ref int p, ref int mon, ref int mday, ref int hour, ref int min, ref int sec, ref long subsec)
    {
        if (!Peek(s, p, '-'))
        {
            return true;
        }
        if (!TwoDigits(s, ref p, out mon) || mon > 15)
        {
            return false;
        }
        if (!Peek(s, p, '-'))
        {
            return true;
        }
        if (!TwoDigits(s, ref p, out mday) || mday > 31)
        {
            return false;
        }
        if (!(Peek(s, p, ' ') || Peek(s, p, 'T')) || p + 1 >= s.Length || !char.IsAsciiDigit(s[p + 1]))
        {
            return true;
        }
        if (!TwoDigits(s, ref p, out hour) || hour > 31 || !Peek(s, p, ':'))
        {
            return false;
        }
        if (!TwoDigits(s, ref p, out min) || min > 63 || !Peek(s, p, ':'))
        {
            return false;
        }
        if (!TwoDigits(s, ref p, out sec) || sec > 63)
        {
            return false;
        }
        if (Peek(s, p, '.'))
        {
            p++;
            var digits = 0;
            while (p + digits < s.Length && digits < 9 && char.IsAsciiDigit(s[p + digits]))
            {
                digits++;
            }
            if (digits == 0)
            {
                return false;
            }
            subsec = long.Parse(s.Slice(p, digits), provider: null);
            for (var scale = digits; scale < 9; scale++)
            {
                subsec *= 10;
            }
            p += digits;
            while (p < s.Length && char.IsAsciiDigit(s[p]))
            {
                p++;
            }
        }
        return true;
    }

    static bool Peek(ReadOnlySpan<char> s, int p, char c) => p < s.Length && s[p] == c;

    /// <summary><c>two_digits</c>, after the separator at <paramref name="p"/>: exactly two digits.</summary>
    static bool TwoDigits(ReadOnlySpan<char> s, ref int p, out int value)
    {
        var start = p + 1;
        value = 0;
        if (start + 2 > s.Length || !char.IsAsciiDigit(s[start]) || !char.IsAsciiDigit(s[start + 1])
            || (start + 2 < s.Length && char.IsAsciiDigit(s[start + 2])))
        {
            return false;
        }
        value = (s[start] - '0') * 10 + (s[start + 1] - '0');
        p = start + 2;
        return true;
    }

    /// <summary>
    /// <c>utc_offset_arg</c>: <c>Z</c>, a military letter, <c>UTC</c> in any case, <c>+HH</c>,
    /// <c>+HHMM</c>, <c>+HH:MM</c>, <c>+HHMMSS</c> or <c>+HH:MM:SS</c>, in seconds east of UTC. Named zones
    /// aren't offsets, and Ruby raises on them here.
    /// </summary>
    public static int? UtcOffset(ReadOnlySpan<char> zone)
    {
        int? hours = null;
        int minutes = 0, seconds = 0;
        switch (zone.Length)
        {
            case 1:
                return zone[0] switch
                {
                    'Z' => 0,
                    >= 'A' and <= 'I' => (zone[0] - 'A' + 1) * 3600,
                    >= 'K' and <= 'M' => (zone[0] - 'A') * 3600,
                    >= 'N' and <= 'Y' => ('M' - zone[0]) * 3600,
                    _ => null,
                };
            case 3 when zone.Equals("UTC", StringComparison.OrdinalIgnoreCase):
                return 0;
            case 3:
                break;
            case 5 or 7:
                if (!SixtyOrLess(zone, 3, out minutes) || (zone.Length == 7 && !SixtyOrLess(zone, 5, out seconds)))
                {
                    return null;
                }
                break;
            case 6 or 9:
                if (zone[3] != ':' || !SixtyOrLess(zone, 4, out minutes)
                    || (zone.Length == 9 && (zone[6] != ':' || !SixtyOrLess(zone, 7, out seconds))))
                {
                    return null;
                }
                break;
            default:
                return null;
        }
        if (zone[0] is not ('+' or '-') || !char.IsAsciiDigit(zone[1]) || !char.IsAsciiDigit(zone[2]))
        {
            return null;
        }
        hours = (zone[1] - '0') * 10 + (zone[2] - '0');
        var offset = hours.Value * 3600 + minutes * 60 + seconds;
        return zone[0] == '-' ? -offset : offset;
    }

    /// <summary>Two digits at <paramref name="at"/>, the first of them 0 to 5.</summary>
    static bool SixtyOrLess(ReadOnlySpan<char> s, int at, out int value)
    {
        value = 0;
        if (!char.IsAsciiDigit(s[at]) || !char.IsAsciiDigit(s[at + 1]) || s[at] > '5')
        {
            return false;
        }
        value = (s[at] - '0') * 10 + (s[at + 1] - '0');
        return true;
    }

    /// <summary>Days from 1970-01-01 to a proleptic Gregorian date (Howard Hinnant's <c>days_from_civil</c>).</summary>
    static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - era * 400;
        var dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146097 + dayOfEra - 719468;
    }
}
