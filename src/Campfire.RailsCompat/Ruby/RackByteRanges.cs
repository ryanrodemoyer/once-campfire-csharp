namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// <c>Rack::Utils.get_byte_ranges(http_range, size)</c> (rack 3.2), where Ruby's string behaviour
/// shows through: each end is read with <c>String#to_i</c>.
/// </summary>
public static class RackByteRanges
{
    /// <summary>
    /// The inclusive ranges within <paramref name="size"/>: null means serve everything, and an empty
    /// list means 416.
    /// </summary>
    public static IReadOnlyList<(long First, long Last)>? Parse(string? header, long size)
    {
        if (size == 0 || header is null || RangeSpec(header) is not { } spec)
        {
            return null;
        }
        if (spec.Count(',') >= 100)
        {
            return null;
        }

        var ranges = new List<(Int128 First, Int128 Last)>();
        foreach (var rangeSpec in RubySplit(spec, SplitComma))
        {
            if (!rangeSpec.Contains('-', StringComparison.Ordinal))
            {
                return null;
            }
            // Split on "-" first, so neither end can be negative.
            var parts = RubySplit(rangeSpec, s => s.IndexOf('-', StringComparison.Ordinal) is var i and >= 0 ? (i, i + 1) : null);
            var r0 = parts.Count > 0 ? parts[0] : null;
            var r1 = parts.Count > 1 ? parts[1] : null;
            Int128 first, last;
            if (string.IsNullOrEmpty(r0))
            {
                if (r1 is null)
                {
                    return null;
                }
                first = Int128.Max(size - RubyString.ToInt128(r1), 0);
                last = size - 1;
            }
            else
            {
                first = RubyString.ToInt128(r0);
                if (r1 is null)
                {
                    last = size - 1;
                }
                else
                {
                    last = RubyString.ToInt128(r1);
                    if (last < first)
                    {
                        return null;
                    }
                    last = Int128.Min(last, size - 1);
                }
            }
            if (first <= last)
            {
                ranges.Add((first, last));
            }
        }

        Int128 total = 0;
        foreach (var (first, last) in ranges)
        {
            total += last - first + 1;
        }
        return total > size ? [] : [.. ranges.Select(r => ((long)r.First, (long)r.Last))];
    }

    /// <summary>
    /// <c>http_range =~ /bytes=([^;]+)/</c>: after the first <c>bytes=</c> that something other than
    /// <c>;</c> follows, up to the next <c>;</c>.
    /// </summary>
    static string? RangeSpec(string header)
    {
        var from = 0;
        int index;
        while ((index = header.IndexOf("bytes=", from, StringComparison.Ordinal)) >= 0)
        {
            var rest = header.AsSpan(index + 6);
            var semicolon = rest.IndexOf(';');
            var spec = semicolon < 0 ? rest : rest[..semicolon];
            if (!spec.IsEmpty)
            {
                return spec.ToString();
            }
            from = index + 1;
        }
        return null;
    }

    /// <summary><c>/,[ \t]*/</c></summary>
    static (int Start, int End)? SplitComma(string s)
    {
        var i = s.IndexOf(',', StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }
        var end = i + 1;
        while (end < s.Length && s[end] is ' ' or '\t')
        {
            end++;
        }
        return (i, end);
    }

    /// <summary><c>String#split</c> with a separator finder: trailing empty fields are dropped.</summary>
    static List<string> RubySplit(string s, Func<string, (int Start, int End)?> find)
    {
        var fields = new List<string>();
        var rest = s;
        while (find(rest) is var (start, end))
        {
            fields.Add(rest[..start]);
            rest = rest[end..];
        }
        fields.Add(rest);
        while (fields.Count > 0 && fields[^1].Length == 0)
        {
            fields.RemoveAt(fields.Count - 1);
        }
        return fields;
    }
}
