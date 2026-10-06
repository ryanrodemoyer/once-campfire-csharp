using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Storage.Media;

/// <summary>Ruby 3.4's <c>Shellwords.split</c>, which turns <c>video_preview_arguments</c> into argv.</summary>
public static partial class Shellwords
{
    // Ruby's \s is ASCII whitespace only.
    [GeneratedRegex("""\G[ \t\n\v\f\r]*(?>([^\x00 \t\n\v\f\r\\'"]+)|'([^\x00']*)'|"((?:[^\x00"\\]|\\[^\x00])*)"|(\\[^\x00]?)|(\S))([ \t\n\v\f\r]|\z)?""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex("""\\([$`"\\\n])""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedEscape();

    [GeneratedRegex("""\\(.)""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Escape();

    /// <summary><c>Shellwords.split(line)</c>: raises on an unmatched quote or a NUL, as Ruby does.</summary>
    public static List<string> Split(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("NUL character");
        }
        var words = new List<string>();
        var field = new StringBuilder();
        for (var match = Token().Match(line); match.Success && match.Length > 0; match = match.NextMatch())
        {
            var (word, single, quoted, escaped, garbage, separator) =
                (match.Groups[1], match.Groups[2], match.Groups[3], match.Groups[4], match.Groups[5], match.Groups[6]);
            if (garbage.Success)
            {
                throw new ArgumentException($"Unmatched quote: {line}");
            }
            field.Append(
                word.Success ? word.Value
                : single.Success ? single.Value
                : quoted.Success ? DoubleQuotedEscape().Replace(quoted.Value, "$1")
                : Escape().Replace(escaped.Value, "$1"));
            if (separator.Success)
            {
                words.Add(field.ToString());
                field.Clear();
            }
        }
        return words;
    }
}
