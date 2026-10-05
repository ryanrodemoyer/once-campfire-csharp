using System.IO.Compression;
using System.Net;
using Campfire.Jobs.OpenGraph;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.Tests.RestrictedHttp;
using Campfire.RichText.Sanitize;
using Campfire.Vectors;

namespace Campfire.Jobs.Tests.OpenGraph;

/// <summary>
/// reference/test/models/opengraph/fetch_test.rb and location_test.rb, against a local server
/// standing in for every public address. It speaks plain HTTP, so the https URLs are http here.
/// </summary>
public sealed class OpenGraphFetchTests
{
    static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    static OpenGraphRoute Html(string host, string path, string body = "<body>ok<body>") =>
        new("GET", host, path, 200, [["Content-Type", "text/html"]], body);

    static OpenGraphRoute Redirect(string host, string path, string location) =>
        new("GET", host, path, 302, [["Location", location]]);

    static OpenGraphFetch Fetch(ScriptedServer server, IResolver resolver) =>
        new(new PrivateNetworkGuard(resolver), (_, cancellationToken) => server.ConnectAsync(cancellationToken));

    [Fact]
    public async Task FetchesValidHtml()
    {
        await using var server = new ScriptedServer([Html("www.example.com", "/")]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        var body = await fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation);

        Assert.Equal("<body>ok<body>"u8.ToArray(), body);
    }

    [Fact]
    public async Task DiscardsOtherContentTypes()
    {
        await using var server = new ScriptedServer([new("GET", "www.example.com", "/", 200, [["Content-Type", "text/plain"]], "I'm not HTML!")]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        Assert.Null(await fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation));
    }

    [Fact]
    public async Task FollowsRedirects()
    {
        await using var server = new ScriptedServer([Redirect("www.example.com", "/", "http://www.other.com/"), Html("www.other.com", "/")]);
        var resolver = FakeResolver.Of("93.184.216.36");
        using var fetch = Fetch(server, resolver);

        var body = await fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation);

        Assert.Equal("<body>ok<body>"u8.ToArray(), body);
        Assert.Equal(["www.other.com"], resolver.Lookups);
    }

    [Fact]
    public async Task DoesNotFollowRedirectsToPrivateNetworks()
    {
        await using var server = new ScriptedServer([Redirect("www.example.com", "/", "http://www.other.com/"), Html("www.other.com", "/")]);
        using var fetch = Fetch(server, FakeResolver.Of("127.0.0.1"));

        await Assert.ThrowsAsync<PrivateNetworkViolationException>(() =>
            fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GivesUpOnRedirectsThatNeverFinish()
    {
        await using var server = new ScriptedServer([Redirect("www.example.com", "/", "http://www.example.com/")]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        await Assert.ThrowsAsync<TooManyRedirectsException>(() =>
            fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation));
        Assert.Equal(OpenGraphFetch.MaxRedirects, server.Requests.Count);
    }

    [Fact]
    public async Task IgnoresLargeResponses()
    {
        await using var server = new ScriptedServer([new("GET", "www.example.com", "/", 200, [["Content-Type", "text/html"], ["Content-Length", "1073741824"]], "too large")]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        Assert.Null(await fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation));
    }

    /// <summary>
    /// A page followed by a gigabyte of zeros, gzipped to about a megabyte, is past the 5MB limit
    /// as soon as that much is inflated; reading stops there.
    /// </summary>
    [Fact]
    public async Task StopsInflatingAGzipBombAtTheLimit()
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize))
        {
            gzip.Write("<meta property=\"og:title\" content=\"Hey!\">"u8);
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 1024; i++)
            {
                gzip.Write(zeros);
            }
        }
        var route = new OpenGraphRoute("GET", "www.example.com", "/", 200, [["Content-Type", "text/html"], ["Content-Encoding", "gzip"]],
            BodyB64: Convert.ToBase64String(compressed.ToArray()));
        await using var server = new ScriptedServer([route]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await fetch.FetchDocumentAsync(RubyUri.Parse("http://www.example.com"), Public, Cancellation));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), started.Elapsed.ToString());
    }

    [Fact]
    public async Task FetchesTheContentType()
    {
        await using var server = new ScriptedServer([new("HEAD", "example.com", "/image.png", 200, [["Content-Type", "image/png"]])]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.35"));

        Assert.Equal("image/png", await fetch.FetchContentTypeAsync(RubyUri.Parse("http://example.com/image.png"), Public, Cancellation));
    }

    [Theory]
    [InlineData("https://www.example.com", true)]
    [InlineData("http://www.example.com", true)]
    [InlineData("~/etc/password", false)]
    [InlineData("ftp://speedtest.tele2.net", false)]
    [InlineData("httpfake", false)]
    [InlineData(" foo", false)]
    [InlineData("https/incorrect", false)]
    public async Task ValidatesUrls(string url, bool valid)
    {
        await using var server = new ScriptedServer([]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        Assert.Equal(valid, await new OpenGraphLocation(fetch, url).IsValidAsync(Cancellation));
    }

    [Theory]
    [InlineData("172.16.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:c0a8:0101")]
    public async Task PrivateNetworkUrlsAreInvalid(string answer)
    {
        await using var server = new ScriptedServer([]);
        using var fetch = Fetch(server, FakeResolver.Of(answer));

        Assert.False(await new OpenGraphLocation(fetch, "https://metadata.internal").IsValidAsync(Cancellation));
    }

    [Theory]
    [InlineData("http://www.example.com/video.mp4")]
    [InlineData("http://www.example.com/archive.tar")]
    [InlineData("https://www.example.com/large.heic")]
    [InlineData("https://www.example.com/image.jpeg")]
    [InlineData("https://www.example.com/malware.exe")]
    [InlineData("https://www.example.com/massiveOS.iso")]
    public async Task DoesNotReadFileUrlsWhenExpectingHtml(string url)
    {
        await using var server = new ScriptedServer([]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        Assert.Null(await new OpenGraphLocation(fetch, url).ReadHtmlAsync(Cancellation));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ReadsValidHtmlAndIgnoresInvalidResponses()
    {
        await using var server = new ScriptedServer(
        [
            Html("www.example.com", "/"),
            new("GET", "www.example.com", "/large", 200, [["Content-Type", "text/html"], ["Content-Length", "1073741824"]], "too large"),
        ]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        Assert.Equal("<body>ok<body>"u8.ToArray(), await new OpenGraphLocation(fetch, "http://www.example.com").ReadHtmlAsync(Cancellation));
        Assert.Null(await new OpenGraphLocation(fetch, "http://www.example.com/large").ReadHtmlAsync(Cancellation));
    }

    /// <summary>reference/test/models/opengraph/metadata_test.rb, over plain HTTP.</summary>
    [Fact]
    public async Task FetchesMetadata()
    {
        const string page = """
            <html>
              <head>
                <meta property="og:url" content="https://example.com">
                <meta property="og:title" content="Hey!">
                <meta property="og:description" content="Hello">
                <meta property="og:image" content="http://example.com/image.png">
              </head>
            </html>
            """;
        await using var server = new ScriptedServer([Html("www.example.com", "/", page), new("HEAD", "example.com", "/image.png", 200, [["Content-Type", "image/png"]])]);
        using var fetch = Fetch(server, FakeResolver.Of("93.184.216.34"));

        var metadata = await OpenGraphMetadata.FromUrlAsync(fetch, "http://www.example.com", Cancellation);
        Assert.True(await metadata.ValidateAsync(fetch, Cancellation));

        Assert.Equal("https://example.com", metadata.Url);
        Assert.Equal("Hey!", metadata.Title);
        Assert.Equal("Hello", metadata.Description);
        Assert.Equal("http://example.com/image.png", metadata.Image);
    }
}
