using System.Text;

namespace Campfire.Data.Searching;

// SearchesController#query (reference/app/controllers/searches_controller.rb):
// `params[:q]&.gsub(/[^[:word:]]/, " ")`. Every code point that isn't one of Onigmo's Unicode word
// characters (alphabetic, marks, decimal digits, connector punctuation, join controls) becomes one
// space, which strips FTS5's quoting and grouping but leaves its AND, OR, NOT and NEAR keywords.
public static class SearchQuery
{
    // `query`: null stays null (no `q` param).
    public static string? Sanitize(string? q)
    {
        if (q is null) return null;

        var builder = new StringBuilder(q.Length);
        foreach (var rune in EnumerateRunes(q))
            builder.Append(IsWord(rune) ? rune.ToString() : " ");
        return builder.ToString();
    }

    // `query.present?`. Sanitize leaves only word characters and spaces, and no word character is
    // `[[:space:]]`, so it's present when anything but a space is left.
    public static bool IsPresent(string? query) =>
        query is not null && query.AsSpan().ContainsAnyExcept(' ');

    // The query `set_messages` searches for: the sanitized `q`, or null when it's blank.
    public static string? Searchable(string? q) => Sanitize(q) is { } query && IsPresent(query) ? query : null;

    // `/[[:word:]]/`, from the table Ruby generated (WordRanges).
    public static bool IsWord(Rune rune)
    {
        var value = rune.Value;
        if (value < 0x80) return char.IsAsciiLetterOrDigit((char)value) || value == '_';

        var pairs = WordRanges.Pairs;
        int low = 0, high = (pairs.Length / 2) - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (value < pairs[2 * middle]) high = middle - 1;
            else if (value > pairs[(2 * middle) + 1]) low = middle + 1;
            else return true;
        }
        return false;
    }

    // The string's code points. A lone surrogate, which a Ruby string can't hold, isn't a word
    // character, so it becomes a space like any other.
    static IEnumerable<Rune> EnumerateRunes(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed);
            yield return status == System.Buffers.OperationStatus.Done ? rune : Rune.ReplacementChar;
            i += consumed;
        }
    }
}
