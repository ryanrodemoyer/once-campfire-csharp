using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.Data.Searching;

namespace Campfire.Data.Tests.Searching;

// SearchQuery, SearchesController#query's port.
public partial class SearchQueryTests
{
    // Oracle/search_word.rb generated WordRanges from Ruby 3.3; reference-rust's table came from
    // the reference image's Ruby 3.4.10. Both are Unicode 15.0.0, and the tables are the same.
    [Fact]
    public void Word_ranges_are_the_reference_rubys()
    {
        var rust = File.ReadAllText(Path.Combine(TestDatabase.RepositoryRoot, "reference-rust", "crates", "campfire", "src", "integrations", "search", "word_ranges.rs"));
        Assert.Contains("Ruby 3.4.10", rust, StringComparison.Ordinal);
        var expected = RustRange().Matches(rust)
            .SelectMany(match => new[] { Hex(match.Groups[1].Value), Hex(match.Groups[2].Value) })
            .ToArray();
        Assert.Equal(770 * 2, expected.Length);
        Assert.Equal(expected, WordRanges.Pairs.ToArray());
    }

    [GeneratedRegex(@"\(0x([0-9A-F]+), 0x([0-9A-F]+)\)")]
    private static partial Regex RustRange();

    static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    // Probed against the reference's Ruby: alphabetic (including letter numbers and circled
    // letters), marks, decimal digits, connector punctuation and join controls.
    [Theory]
    [InlineData("a"), InlineData("Z"), InlineData("0"), InlineData("_"), InlineData("é"), InlineData("ß"), InlineData("日"), InlineData("‿")]
    [InlineData("́"), InlineData("٣"), InlineData("ǅ"), InlineData("ʰ"), InlineData("Ⅻ"), InlineData("Ⓐ"), InlineData("‍"), InlineData("𠀀")]
    public void Word_characters(string character) => Assert.True(SearchQuery.IsWord(Rune.GetRuneAt(character, 0)));

    [Theory]
    [InlineData(" "), InlineData("-"), InlineData("*"), InlineData("\""), InlineData(" "), InlineData("❤"), InlineData("€"), InlineData("½"), InlineData("²"), InlineData("😀")]
    public void Not_word_characters(string character) => Assert.False(SearchQuery.IsWord(Rune.GetRuneAt(character, 0)));

    [Fact]
    public void Every_code_point_outside_a_word_becomes_one_space()
    {
        Assert.Equal("héllo wörld_1 ２ 日本語 ‿ a b ️   é", SearchQuery.Sanitize("héllo wörld_1 ２ 日本語 ‿ a-b ️ ❤ é"));
        Assert.Equal(" quoted  OR NEAR x  ", SearchQuery.Sanitize("\"quoted\" OR NEAR(x*)"));
        Assert.Equal("a b", SearchQuery.Sanitize("a😀b"));
        Assert.Equal("a b", SearchQuery.Sanitize("a\ud800b"));
        Assert.Equal("", SearchQuery.Sanitize(""));
        Assert.Null(SearchQuery.Sanitize(null));
    }

    [Fact]
    public void Blank_queries_are_not_searched()
    {
        Assert.Null(SearchQuery.Searchable(null));
        Assert.Null(SearchQuery.Searchable(""));
        Assert.Null(SearchQuery.Searchable(" -*\"() "));
        Assert.Equal(" hello ", SearchQuery.Searchable("(hello)"));
    }
}
