using System.Text;
using Campfire.Web.Assets;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Assets;

// The bundle served like ActionDispatch::Static, against how the reference answered the same
// requests (static_responses.json; last-modified is left out of it, being an mtime).
public sealed class StaticFilesTests
{
    static readonly StaticFiles Static = new(ReferenceAssets.Bundle);

    [Fact]
    public void Public_files_and_assets_are_served_like_action_dispatch_static()
    {
        foreach (var expected in ReferenceAssets.JsonFixture("static_responses.json").EnumerateArray())
        {
            var env = expected.GetProperty("env");
            var request = new StaticRequest(expected.GetProperty("method").GetString()!, expected.GetProperty("path").GetString()!)
            {
                Range = env.TryGetProperty("HTTP_RANGE", out var range) ? range.GetString() : null,
                AcceptEncoding = env.TryGetProperty("HTTP_ACCEPT_ENCODING", out var encoding) ? encoding.GetString() : null,
            };
            var label = $"{request.Method} {request.Path} {env}";
            var status = expected.GetProperty("status").GetInt32();
            var headers = expected.GetProperty("headers").EnumerateObject()
                .ToDictionary(header => header.Name, header => header.Value.GetString());

            var response = Static.Serve(request);

            // The reference's fallthrough app answered 404 with x-cascade: pass.
            if (status == 404 && headers.ContainsKey("x-cascade"))
            {
                Assert.True(response is null, $"{label} should fall through");
                continue;
            }

            Assert.True(response is not null, $"{label} isn't served");
            Assert.Equal((label, status), (label, response.Status));

            var ours = response.Headers.Where(header => header.Key != "last-modified").ToDictionary(header => header.Key, header => (string?)header.Value);
            if (request.Path == "/assets/.manifest.json")
            {
                // Same entries in a different order; Served_manifest_has_the_reference_entries_and_length.
                Assert.Equal(headers, ours);
                continue;
            }

            Assert.Equal($"{label} {Describe(headers)}", $"{label} {Describe(ours)}");
            Assert.Equal((label, expected.GetProperty("body_sha256").GetString()), (label, ReferenceAssets.Sha256(response.Body.Span)));
        }
    }

    static string Describe(Dictionary<string, string?> headers) =>
        string.Join("; ", headers.OrderBy(header => header.Key, StringComparer.Ordinal).Select(header => $"{header.Key}: {header.Value}"));

    [Fact]
    public void Last_modified_round_trips_to_a_304()
    {
        var lastModified = Static.Serve(new StaticRequest("GET", "/robots.txt"))!.Header("last-modified");
        Assert.Equal("Sat, 26 Sep 2026 12:23:14 GMT", lastModified);

        var notModified = Static.Serve(new StaticRequest("GET", "/robots.txt") { IfModifiedSince = lastModified })!;

        Assert.Equal(304, notModified.Status);
        Assert.Empty(notModified.Headers);
        Assert.True(notModified.Body.IsEmpty);
    }

    [Fact]
    public void Head_requests_have_no_body()
    {
        var response = Static.Serve(new StaticRequest("HEAD", "/robots.txt"))!;

        Assert.Equal(200, response.Status);
        Assert.True(response.Body.IsEmpty);
        Assert.Equal("99", response.Header("content-length"));
    }

    [Fact]
    public void Multiple_ranges_are_multipart_with_the_files_type()
    {
        var response = Static.Serve(new StaticRequest("GET", "/assets/56k-67359aa6.mp3") { Range = "bytes=0-1, 4-5" })!;

        Assert.Equal(206, response.Status);
        // Rack sets multipart/byteranges, then Static overwrites it with the file's type.
        Assert.Equal("audio/mpeg", response.Header("content-type"));
        var body = Encoding.Latin1.GetString(response.Body.Span);
        Assert.StartsWith("\r\n--AaB03x\r\ncontent-type: audio/mpeg\r\ncontent-range: bytes 0-1/133677\r\n\r\n", body, StringComparison.Ordinal);
        Assert.EndsWith("\r\n--AaB03x--\r\n", body, StringComparison.Ordinal);
        Assert.Equal(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), response.Header("content-length"));
    }

    [Fact]
    public void Precompressed_files_are_negotiated_by_accept_encoding()
    {
        var digested = ReferenceAssets.Bundle.DigestedPath("lexxy.js")!;
        var path = $"/assets/{digested}";
        Assert.False(ReferenceAssets.Bundle.Files.ContainsKey(path + ".br"));

        // No digested asset has a .br or .gz sibling, so negotiation is exercised on a bundle
        // written with one.
        var directory = Directory.CreateTempSubdirectory("campfire-assets-");
        try
        {
            ReferenceAssets.Bundle.WriteTo(directory.FullName);
            File.WriteAllBytes(Path.Combine(directory.FullName, "public", "robots.txt.gz"), [1, 2, 3]);
            var files = new StaticFiles(AssetBundle.Load(directory.FullName));

            var gzip = files.Serve(new StaticRequest("GET", "/robots.txt") { AcceptEncoding = "br;q=1.0, GZIP;q=0.5" })!;
            Assert.Equal("gzip", gzip.Header("content-encoding"));
            Assert.Equal("accept-encoding", gzip.Header("vary"));
            Assert.Equal("text/plain", gzip.Header("content-type"));
            Assert.Equal([1, 2, 3], gzip.Body.ToArray());

            var identity = files.Serve(new StaticRequest("GET", "/robots.txt") { AcceptEncoding = "gzipped, x-gzip2" })!;
            Assert.Null(identity.Header("content-encoding"));
            Assert.Equal("accept-encoding", identity.Header("vary"));
            Assert.Equal(99, identity.Body.Length);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task An_http_request_is_answered_with_the_static_response()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/robots.txt";
        context.Request.Headers.Range = "bytes=0-9";
        context.Response.Body = new MemoryStream();

        Assert.True(await Static.TryServeAsync(context));

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 0-9/99", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(StaticFiles.CacheControl, context.Response.Headers.CacheControl.ToString());
        Assert.Equal(10, context.Response.Body.Length);
    }

    [Fact]
    public async Task An_http_request_for_an_unknown_path_falls_through()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/rooms";

        Assert.False(await Static.TryServeAsync(context));
    }

    [Theory]
    [InlineData("/robots.txt")]
    [InlineData("/%72obots.txt")]
    [InlineData("/assets/../robots.txt")]
    [InlineData("/../../robots.txt")]
    [InlineData("//robots.txt/")]
    public void Paths_are_cleaned_like_rack(string path)
    {
        Assert.Equal(200, Static.Serve(new StaticRequest("GET", path))?.Status);
    }

    [Theory]
    [InlineData("POST", "/robots.txt")]
    [InlineData("GET", "/robots.txt%00")]
    [InlineData("GET", "/robots%2etxt%ff")]
    [InlineData("GET", "/assets")]
    public void Other_requests_fall_through(string method, string path)
    {
        Assert.Null(Static.Serve(new StaticRequest(method, path)));
    }

    // Expected values are Rack::Utils.get_byte_ranges(range, 50) from rack 3.2.6.
    [Theory]
    [InlineData("bytes=0-9", "0-9")]
    [InlineData("bytes=-5", "45-49")]
    [InlineData("bytes=100-", "")]
    [InlineData("bytes=5-2", null)]
    [InlineData("bytes=0-1, 4-5", "0-1,4-5")]
    [InlineData("bytes=0-1,\t4-5,", "0-1,4-5")]
    [InlineData("bytes=,0-1", null)]
    [InlineData("bytes=-", null)]
    [InlineData("bytes=--5", "")]
    [InlineData("bytes=1_0-2_0", "10-20")]
    [InlineData("bytes=a-3", "0-3")]
    [InlineData(" bytes=0-0;x", "0-0")]
    [InlineData("bytes=;bytes=2-3", "2-3")]
    [InlineData("items=0-1", null)]
    [InlineData("bytes=0-999", "0-49")]
    [InlineData("bytes=50-60", "")]
    [InlineData("bytes=0-30,0-30", "")]
    [InlineData("bytes=-0", "")]
    [InlineData("bytes=3-3-9", "3-3")]
    [InlineData("bytes= 2- 4", "2-4")]
    public void Byte_ranges_parse_like_rack(string range, string? expected)
    {
        var ranges = RackUtils.GetByteRanges(range, 50);

        Assert.Equal(expected, ranges is null ? null : string.Join(',', ranges.Select(r => $"{r.Start}-{r.End}")));
    }
}
