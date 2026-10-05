using System.Text;

namespace Campfire.RailsCompat.Formatting;

/// <summary>Active Support's <c>String#truncate</c> and <c>Array#to_sentence</c>.</summary>
public static class TextHelpers
{
    /// <summary>
    /// <c>String#truncate(length, omission:, separator:)</c>, counting characters as Ruby does
    /// (codepoints): at most <paramref name="length"/> of them, omission included, cut at the last
    /// <paramref name="separator"/> that leaves room when one is given. The view helper
    /// <c>truncate</c> escapes the result; escape it on output.
    /// </summary>
    public static string Truncate(string text, int length, string omission = "...", string? separator = null)
    {
        var characters = Codepoints(text);
        if (characters.Length <= length)
        {
            return text;
        }
        var stop = length - Codepoints(omission).Length;
        if (separator is not null && RubyRindex(characters, Codepoints(separator), stop) is { } index)
        {
            stop = index;
        }
        // self[0, stop] is nil for a negative stop, and "#{nil}" is empty.
        var builder = new StringBuilder();
        foreach (var rune in characters.AsSpan(0, Math.Max(stop, 0)))
        {
            builder.Append(rune.ToString());
        }
        return builder.Append(omission).ToString();
    }

    /// <summary>
    /// <c>Array#to_sentence</c> with the default English connectors, each of which can be replaced:
    /// <c>A</c>, <c>A and B</c>, <c>A, B, and C</c>.
    /// </summary>
    public static string ToSentence(
        IReadOnlyList<string> items,
        string wordsConnector = ", ",
        string twoWordsConnector = " and ",
        string lastWordConnector = ", and ") => items.Count switch
        {
            0 => "",
            1 => items[0],
            2 => items[0] + twoWordsConnector + items[1],
            _ => string.Join(wordsConnector, items.Take(items.Count - 1)) + lastWordConnector + items[^1],
        };

    static Rune[] Codepoints(string s) => [.. s.EnumerateRunes()];

    /// <summary>
    /// <c>String#rindex(substring, position)</c>: the last match starting at or before
    /// <paramref name="position"/>, which counts from the end when negative.
    /// </summary>
    static int? RubyRindex(Rune[] s, Rune[] substring, int position)
    {
        if (position < 0)
        {
            position += s.Length;
            if (position < 0)
            {
                return null;
            }
        }
        for (var start = Math.Min(position, s.Length - substring.Length); start >= 0; start--)
        {
            if (s.AsSpan(start, substring.Length).SequenceEqual(substring))
            {
                return start;
            }
        }
        return null;
    }
}
