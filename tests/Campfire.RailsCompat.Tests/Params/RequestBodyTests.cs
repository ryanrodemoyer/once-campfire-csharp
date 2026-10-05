using System.Text;
using Campfire.RailsCompat.Params;

namespace Campfire.RailsCompat.Tests.Params;

public class RequestBodyTests
{
    static Task<ParsedBody> Parse(string method, string? contentType, string body, bool withLength = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return RequestBody.ParseAsync(method, contentType, withLength ? bytes.Length : null, new MemoryStream(bytes), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UrlencodedForms()
    {
        using var parsed = await Parse("POST", "application/x-www-form-urlencoded", "a[b]=1&c=2");
        Assert.Equal("""{"a":{"b":"1"},"c":"2"}""", parsed.Params.ToString());
        Assert.Equal("a[b]=1&c=2"u8.ToArray(), parsed.Raw);
    }

    [Fact]
    public async Task APostWithoutAContentTypeIsAForm()
    {
        using var post = await Parse("POST", null, "Hello!");
        Assert.Equal("""{"Hello!":null}""", post.Params.ToString());
        using var put = await Parse("PUT", null, "Hello!");
        Assert.Equal("{}", put.Params.ToString());
        Assert.Equal("Hello!"u8.ToArray(), put.Raw);
    }

    [Fact]
    public async Task OtherBodiesAreLeftRaw()
    {
        using var parsed = await Parse("POST", "text/plain", "a=1");
        Assert.Equal("{}", parsed.Params.ToString());
        Assert.Equal("a=1"u8.ToArray(), parsed.Raw);
    }

    [Fact]
    public async Task SafarisTrailingNulIsDropped()
    {
        using var parsed = await Parse("POST", "application/x-www-form-urlencoded", "a=1\0");
        Assert.Equal("""{"a":"1"}""", parsed.Params.ToString());
    }

    [Fact]
    public async Task FormLimits()
    {
        var many = string.Join('&', Enumerable.Repeat("a=1", QueryParser.FormParamsLimit + 1));
        using var tooMany = await Parse("POST", "application/x-www-form-urlencoded", many);
        Assert.Equal(ParamErrorKind.Limit, tooMany.Error!.Kind);
        Assert.Throws<ParamException>(() => tooMany.Params);

        var exactly = string.Join('&', Enumerable.Repeat("a=1", QueryParser.FormParamsLimit));
        using var fits = await Parse("POST", "application/x-www-form-urlencoded", exactly);
        Assert.Null(fits.Error);

        using var tooBig = await Parse("POST", "application/x-www-form-urlencoded", "a=" + new string('x', QueryParser.FormByteSizeLimit));
        Assert.Equal(ParamErrorKind.Limit, tooBig.Error!.Kind);
    }

    [Fact]
    public async Task BadFormEncodingIsAnError()
    {
        using var parsed = await Parse("POST", "application/x-www-form-urlencoded", "a=%zz");
        Assert.Equal(ParamErrorKind.Invalid, parsed.Error!.Kind);
        Assert.Equal(400, parsed.Error.StatusCode);
    }

    [Fact]
    public async Task JsonBodies()
    {
        using var parsed = await Parse("POST", "application/json; charset=utf-8", """{"url":"x","n":[1,null,"y"],"h":{"k":null},"t":true,"f":1.5}""");
        Assert.Equal("""{"url":"x","n":[1,"y"],"h":{"k":null},"t":true,"f":1.5}""", parsed.Params.ToString());
        Assert.Equal(1L, Assert.IsType<long>(parsed.Params.GetArray("n")![0]));

        using var array = await Parse("POST", "application/json", "[1,2]");
        Assert.Equal("""{"_json":[1,2]}""", array.Params.ToString());

        using var synonym = await Parse("PATCH", "text/x-json", "\"s\"");
        Assert.Equal("""{"_json":"s"}""", synonym.Params.ToString());
    }

    [Fact]
    public async Task ChunkedJsonIsParsedToo()
    {
        using var parsed = await Parse("POST", "application/json", """{"a":1}""", withLength: false);
        Assert.Equal("""{"a":1}""", parsed.Params.ToString());
    }

    [Fact]
    public async Task EmptyJsonBodiesAreEmptyParams()
    {
        using var parsed = await Parse("POST", "application/json", "");
        Assert.Equal("{}", parsed.Params.ToString());
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("""{"a":1,}""")]
    [InlineData("NaN")]
    [InlineData("""{"a":1} x""")]
    [InlineData("\"\\ud800\"")]
    public async Task MalformedJsonIsAParseError(string json)
    {
        using var parsed = await Parse("POST", "application/json", json);
        Assert.Equal(ParamErrorKind.Parse, parsed.Error!.Kind);
    }

    [Fact]
    public void JsonLikeRuby()
    {
        // json 2.21 skips comments, keeps the last of duplicate keys, and keeps integers exact.
        Assert.Equal("""{"a":2}""", ParamBuilder.FromJson("""/* c */ {"a":1, "a":2} // x"""u8).ToString());
        Assert.Equal("""{"_json":123456789012345678901234567890}""", ParamBuilder.FromJson("123456789012345678901234567890"u8).ToString());
        Assert.Equal(100.0, Assert.IsType<double>(ParamBuilder.FromJson("1E2"u8)["_json"]));
        Assert.Equal(double.PositiveInfinity, Assert.IsType<double>(ParamBuilder.FromJson("1e400"u8)["_json"]));
        Assert.Equal(0L, Assert.IsType<long>(ParamBuilder.FromJson("-0"u8)["_json"]));
    }

    [Fact]
    public void JsonNestingLimitMatchesRuby()
    {
        // JSON.parse's max_nesting of 100 accepts 101 levels and refuses 102.
        ParamBuilder.FromJson(Encoding.ASCII.GetBytes(new string('[', 101) + new string(']', 101)));
        var deep = Encoding.ASCII.GetBytes(new string('[', 102) + new string(']', 102));
        Assert.Equal(ParamErrorKind.Parse, Assert.Throws<ParamException>(() => ParamBuilder.FromJson(deep)).Kind);
    }

    [Fact]
    public async Task AnUnparseableContentTypeIsNotAcceptable()
    {
        using var parsed = await Parse("POST", "nonsense", "a=1");
        Assert.Equal(ParamErrorKind.InvalidMimeType, parsed.Error!.Kind);
        Assert.Equal(406, parsed.Error.StatusCode);

        // Rails only looks at the Content-Type when there's a body.
        using var empty = await Parse("POST", "nonsense", "");
        Assert.Null(empty.Error);
    }

    [Fact]
    public async Task OversizedRawBodiesAreTooLarge()
    {
        var bytes = new byte[100];
        await Assert.ThrowsAsync<RequestBodyTooLargeException>(() =>
            RequestBody.ParseAsync("POST", "application/json", bytes.Length, new MemoryStream(bytes), maxBufferedBody: 10, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestBodyTooLargeException>(() =>
            RequestBody.ParseAsync("POST", "text/plain", bytes.Length, new MemoryStream(bytes), maxBufferedBody: 10, cancellationToken: TestContext.Current.CancellationToken));
        using var exact = await RequestBody.ParseAsync("POST", "text/plain", bytes.Length, new MemoryStream(bytes), maxBufferedBody: 100, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(100, exact.Raw.Length);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Multipart/Form-Data ; boundary=x", "multipart/form-data")]
    [InlineData("text/plain,charset=x", "text/plain")]
    [InlineData(" application/json", " application/json")]
    public void MediaTypeLikeRack(string? contentType, string? mediaType)
    {
        Assert.Equal(mediaType, RequestBody.MediaType(contentType));
    }
}
