using Campfire.Web.Assets;

namespace Campfire.Web.Tests.Assets;

// Expected values are Ruby 3.4's own Pathname and File output for the same inputs.
public sealed class RubyPathTests
{
    [Theory]
    [InlineData("a/b", "../c", "a/c")]
    [InlineData(".", "foo", "foo")]
    [InlineData("a", "./b/../c", "a/b/../c")]
    [InlineData("a/b", "../../../c", "../c")]
    [InlineData("a", "b//c", "a/b//c")]
    [InlineData("a/b", "..", "a")]
    [InlineData("a", "../", ".")]
    [InlineData(".", "../x", "../x")]
    [InlineData("a/b/c", "../d/./e.svg", "a/b/d/./e.svg")]
    [InlineData("a", "/abs", "/abs")]
    [InlineData("..", "../x", "../../x")]
    [InlineData("a/..", "../x", "a/../../x")]
    public void Plus_matches_pathname(string left, string right, string expected)
    {
        Assert.Equal(expected, RubyPath.Plus(left, right));
    }

    [Theory]
    [InlineData("../a/./b", "../a/b")]
    [InlineData("x/../../a", "../a")]
    [InlineData("a/b/..", "a")]
    [InlineData("a/..", ".")]
    public void Cleanpath_matches_relative_path_from_the_empty_path(string path, string expected)
    {
        Assert.Equal(expected, RubyPath.Cleanpath(path));
    }

    [Theory]
    [InlineData("a/b.css", "a", "b.css", ".css")]
    [InlineData("b.css", ".", "b.css", ".css")]
    [InlineData(".x", ".", ".x", "")]
    [InlineData("..a", ".", "..a", "")]
    [InlineData("a.b.", ".", "a.b.", ".")]
    [InlineData("a/", ".", "a", "")]
    [InlineData("/", "/", "/", "")]
    [InlineData("a//b.js", "a", "b.js", ".js")]
    [InlineData("lexxy.js.br", ".", "lexxy.js.br", ".br")]
    public void File_name_parts_match_ruby(string path, string dirname, string basename, string extname)
    {
        Assert.Equal((dirname, basename, extname), (RubyPath.Dirname(path), RubyPath.Basename(path), RubyPath.Extname(path)));
    }
}
