using System.Text;
using Campfire.RailsCompat.Params;

namespace Campfire.RailsCompat.Tests.Params;

public class ParamBuilderTests
{
    static string Parse(string query) => ParamBuilder.FromQueryString(query).ToString();

    static ParamErrorKind ErrorOf(string query) => Assert.Throws<ParamException>(() => ParamBuilder.FromQueryString(query)).Kind;

    [Fact]
    public void FlatPairs()
    {
        Assert.Equal("""{"a":"1","b":"2"}""", Parse("a=1&b=2"));
        Assert.Equal("{}", Parse(""));
        Assert.Equal("""{"a":null}""", Parse("a"));
        Assert.Equal("""{"a":"2"}""", Parse("a=1&a=2"));
        Assert.Equal("""{"a":"1","b":"2"}""", Parse("a=1&  b=2"));
        Assert.Equal("{}", Parse("=1"));
    }

    [Fact]
    public void LaterValuesKeepTheFirstPosition()
    {
        Assert.Equal("""{"a":"3","b":"2"}""", Parse("a=1&b=2&a=3"));
    }

    [Fact]
    public void Arrays()
    {
        Assert.Equal("""{"a":["1","2"]}""", Parse("a[]=1&a[]=2"));
        Assert.Equal("""{"a":[]}""", Parse("a[]"));
        Assert.Equal("""{"a":[{"b":"1","c":"2"},{"b":"3"}]}""", Parse("a[][b]=1&a[][c]=2&a[][b]=3"));
    }

    [Fact]
    public void TypeConflictsRaise()
    {
        Assert.Equal(ParamErrorKind.Type, ErrorOf("a=1&a[]=2"));
        Assert.Equal(ParamErrorKind.Type, ErrorOf("a[b]=1&a[b][c]=2"));
    }

    [Fact]
    public void DepthLimit()
    {
        ParamBuilder.FromQueryString("a" + string.Concat(Enumerable.Repeat("[b]", 99)) + "=1");
        Assert.Equal(ParamErrorKind.TooDeep, ErrorOf("a" + string.Concat(Enumerable.Repeat("[b]", 100)) + "=1"));
    }

    [Fact]
    public void BadEncodingRaisesUnlessTheKeyIsEmpty()
    {
        Assert.Equal(ParamErrorKind.Invalid, ErrorOf("a=%"));
        Assert.Equal(ParamErrorKind.Invalid, ErrorOf("a=%FF"));
        Assert.Equal(ParamErrorKind.Invalid, ErrorOf("a[%FF]=1"));
        Assert.Equal("{}", Parse("=%FF"));
        Assert.Equal("""{"café":"✓"}""", Parse("caf%C3%A9=%E2%9C%93"));
    }

    [Fact]
    public void RawBytesInTheQueryAreCheckedLikeEscapes()
    {
        var query = Encoding.Latin1.GetBytes("a=ÿ");
        Assert.Equal(ParamErrorKind.Invalid, Assert.Throws<ParamException>(() => ParamBuilder.FromQueryString(query)).Kind);
        Assert.Equal("{}", ParamBuilder.FromQueryString(Encoding.Latin1.GetBytes("=ÿ")).ToString());
        Assert.Equal("""{"é":"✓"}""", ParamBuilder.FromQueryString("é=✓"u8.ToArray()).ToString());
    }

    [Fact]
    public void PairsInOtherEncodingsAreCheckedInThem()
    {
        var latin1 = new ParamPair(Encoding.Latin1.GetBytes("name"), new byte[] { 0xE9 }, Encoding.Latin1);
        Assert.Equal("é", ParamBuilder.FromPairs([latin1]).GetString("name"));

        var ascii = new ParamPair(Encoding.ASCII.GetBytes("name"), new byte[] { 0xE9 }, Encoding.ASCII);
        Assert.Equal(ParamErrorKind.Invalid, Assert.Throws<ParamException>(() => ParamBuilder.FromPairs([ascii])).Kind);
    }

    [Fact]
    public void FilesNestLikeStrings()
    {
        using var file = UploadedFile.FromBytes("me.png", "image/png", "PNG"u8);
        var parameters = ParamBuilder.FromPairs([ParamPair.Text("user[name]", "Jo"), ParamPair.File("user[avatar]", file)]);
        Assert.Same(file, parameters.GetHash("user")!.GetFile("avatar"));
        Assert.Equal(ParamErrorKind.Type, Assert.Throws<ParamException>(() =>
            ParamBuilder.FromPairs([ParamPair.File("a", file), ParamPair.Text("a[b]", "1")])).Kind);
    }
}
