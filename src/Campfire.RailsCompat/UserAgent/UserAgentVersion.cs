namespace Campfire.RailsCompat.UserAgent;

/// <summary>One segment of <c>UserAgent::Version#to_a</c>: a digit run (leading zeros stripped) or a string.</summary>
public readonly record struct VersionSegment(bool IsInteger, string Text)
{
    public static VersionSegment OfDigits(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return new VersionSegment(true, trimmed.Length == 0 ? "0" : trimmed);
    }

    public static VersionSegment OfText(string text) => new(false, text);

    /// <summary>The integer value, or null for a string segment or one too large for a long.</summary>
    public long? AsInt64() => IsInteger && long.TryParse(Text, out var value) ? value : null;

    public override string ToString() => (IsInteger ? "i:" : "s:") + Text;
}

/// <summary>
/// <c>UserAgent::Version</c> from the useragent gem (0.16.11). Equality is string equality;
/// <see cref="RubyCompare"/> is the gem's <c>&lt;=&gt;</c>, which is not a total order (a
/// non-numeric version sorts below everything it isn't equal to, from either side).
/// </summary>
public sealed class UserAgentVersion : IEquatable<UserAgentVersion>
{
    public static readonly UserAgentVersion Empty = new("");

    readonly bool blank;
    readonly bool comparable;

    public UserAgentVersion(string text)
    {
        Text = text;
        blank = text.All(RubyText.IsSpace);
        var digits = text.TakeWhile(char.IsAsciiDigit).Count();
        comparable = !blank && digits > 0 && (digits == text.Length || text[digits] == '.');
    }

    public string Text { get; }

    /// <summary><c>Version#nil?</c>: the string is empty or whitespace.</summary>
    public bool IsNil => blank;

    /// <summary><c>version.to_s.present?</c>.</summary>
    public bool IsPresent => RubyText.IsPresent(Text);

    /// <summary><c>Version#to_a</c>.</summary>
    public IReadOnlyList<VersionSegment> ToA()
    {
        if (blank)
        {
            return [];
        }
        return comparable ? ScanSequences(Text) : [VersionSegment.OfText(Text)];
    }

    /// <summary><c>Version#&lt;=&gt;</c>: only the first six segments count.</summary>
    public int RubyCompare(UserAgentVersion other)
    {
        if (!comparable)
        {
            return Text == other.Text ? 0 : -1;
        }

        var (ours, theirs) = (ToA(), other.ToA());
        var zero = VersionSegment.OfDigits("0");
        for (var i = 0; i < 6; i++)
        {
            var a = i < ours.Count ? ours[i] : zero;
            var b = i < theirs.Count ? theirs[i] : zero;
            if (a.IsInteger != b.IsInteger)
            {
                return a.IsInteger ? 1 : -1;
            }
            if (a == b)
            {
                continue;
            }
            if (a.IsInteger)
            {
                var byLength = a.Text.Length.CompareTo(b.Text.Length);
                return Math.Sign(byLength != 0 ? byLength : string.CompareOrdinal(a.Text, b.Text));
            }
            return Math.Sign(string.CompareOrdinal(a.Text, b.Text));
        }
        return 0;
    }

    public static bool operator <(UserAgentVersion a, UserAgentVersion b) => a.RubyCompare(b) < 0;

    public static bool operator >(UserAgentVersion a, UserAgentVersion b) => a.RubyCompare(b) > 0;

    public bool Equals(UserAgentVersion? other) => other is not null && Text == other.Text;

    public override bool Equals(object? obj) => Equals(obj as UserAgentVersion);

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Text;

    /// <summary><c>str.scan(/\d+|[A-Za-z][0-9A-Za-z-]*$/)</c>.</summary>
    static List<VersionSegment> ScanSequences(string text)
    {
        var segments = new List<VersionSegment>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
                segments.Add(VersionSegment.OfDigits(text[start..i]));
            }
            else if (char.IsAsciiLetter(text[i]))
            {
                var end = i + 1;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '-'))
                {
                    end++;
                }
                if (end == text.Length)
                {
                    segments.Add(VersionSegment.OfText(text[i..]));
                    i = end;
                }
                else
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
        return segments;
    }
}
