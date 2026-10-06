using System.Text;
using Campfire.Jobs.OpenGraph;

namespace Campfire.Jobs.Tests.OpenGraph;

/// <summary>
/// reference/test/models/opengraph/document_test.rb, and the libxml2 behaviour the Rust port
/// probed against the reference's Nokogiri 1.19.4 (opengraph/html.rs and metadata.rs tests).
/// </summary>
public sealed class OpenGraphDocumentTests
{
    const string ogPage = "<html><head><meta property=\"og:url\" content=\"https://example.com\"><meta property=\"og:title\" content=\"Hey!\"><meta property=\"og:description\" content=\"desc..\"><meta property=\"og:image\" content=\"https://example.com/image.png\"></head></html>";

    static List<KeyValuePair<string, string>> Attributes(string html) => OpenGraphDocument.OpenGraphAttributes(Encoding.UTF8.GetBytes(html));

    [Fact]
    public void ExtractsOpenGraphTagsUsingThePropertyAttribute()
    {
        Assert.Equal(
            [new("title", "Hey!"), new("url", "https://example.com"), new("image", "https://example.com/image.png"), new("description", "desc..")],
            Attributes(ogPage));
    }

    [Fact]
    public void ExtractsOpenGraphTagsUsingTheNameAttribute()
    {
        Assert.Equal(Attributes(ogPage), Attributes(ogPage.Replace("property=", "name=", StringComparison.Ordinal)));
    }

    [Fact]
    public void DropsNonAsciiWithoutAMetaEncoding()
    {
        var html = "<html><head><meta name=\"og:url\" content=\"https://example.com\"><meta name=\"og:title\" content=\"Hey!\"><meta name=\"og:description\" content=\"Hello \u00E2\u0080\u0099World\"><meta name=\"og:image\" content=\"https://example.com/image.png\"></head></html>";

        Assert.Equal(new KeyValuePair<string, string>("description", "Hello World"), Attributes(html)[3]);
        Assert.Empty(OpenGraphDocument.OpenGraphAttributes(null));
    }

    static string? Title(string html) =>
        LegacyHtml.MetaElements("<meta charset=utf-8>" + html).LastOrDefault(m => m["property"] == "og:title")?["content"] is { Length: > 0 } content
            ? content
            : null;

    [Theory]
    [InlineData("&apos;", "a'b")]
    [InlineData("&eacute", "a&eacuteb")]
    [InlineData("&eacute;x", "a\u00E9xb")]
    [InlineData("&#233", "a\u00E9b")]
    [InlineData("&#233x", "a\u00E9xb")]
    [InlineData("&#xE9", "a\u0E9B")]
    [InlineData("&#xe9;", "a\u00E9b")]
    [InlineData("&AMP;", "a&AMP;b")]
    [InlineData("&Eacute;", "a\u00C9b")]
    [InlineData("&unknown;", "a&unknown;b")]
    [InlineData("& x", "a& xb")]
    [InlineData("&#65;&#x41;", "aAAb")]
    [InlineData("&#128;", "a\u0080b")]
    [InlineData("&#150;", "a\u0096b")]
    [InlineData("&#xD800;", "a")]
    [InlineData("&#1114112;", "a")]
    [InlineData("&lt", "a&ltb")]
    [InlineData("&amp;amp;", "a&amp;b")]
    [InlineData("&hellip;", "a\u2026b")]
    [InlineData("&nbsp", "a&nbspb")]
    [InlineData("&#;", "a")]
    [InlineData("&#x;", "a")]
    public void DecodesReferencesLikeLibxml2(string reference, string expected)
    {
        Assert.Equal(expected, Title($"<meta property=\"og:title\" content=\"a{reference}b\">"));
    }

    [Theory]
    [InlineData("<script><meta property=\"og:title\" content=\"in script\"></script><meta property=\"og:title\" content=\"after\">", "after")]
    [InlineData("<style><meta property=\"og:title\" content=\"in style\"></style>", null)]
    [InlineData("<textarea><meta property=\"og:title\" content=\"in textarea\"></textarea>", "in textarea")]
    [InlineData("<title><meta property=\"og:title\" content=\"in title\"></title>", "in title")]
    [InlineData("<noscript><meta property=\"og:title\" content=\"in noscript\"></noscript>", "in noscript")]
    [InlineData("<template><meta property=\"og:title\" content=\"in template\"></template>", "in template")]
    [InlineData("<svg><meta property=\"og:title\" content=\"in svg\"></svg>", "in svg")]
    [InlineData("<!-- <meta property=\"og:title\" content=\"comment\"> --><p>", null)]
    [InlineData("<meta property=og:title content=unquoted>", "unquoted")]
    [InlineData("<meta property=\"og:title\" content=\"line1\r\nline2\">", "line1\r\nline2")]
    [InlineData("<meta property='og:title' content='single'>", "single")]
    [InlineData("<meta property = \"og:title\" content = \"spaced\">", "spaced")]
    [InlineData("<META PROPERTY=\"og:title\" CONTENT=\"upper\">", "upper")]
    [InlineData("<meta property=\"og:title\"content=\"nospace\">", "nospace")]
    [InlineData("<meta/property=\"og:title\"/content=\"slashes\">", null)]
    [InlineData("<meta property=\"og:title\" content=\"<b>tag</b>\">", "<b>tag</b>")]
    [InlineData("<meta property=\"og:title\" content=\"a\">b\">", "a")]
    [InlineData("<meta property=\"og:title\" content=\"unterminated>", "unterminated>")]
    [InlineData("<meta property=\"og:title\" content=unq\"uoted>", "unq\"uoted")]
    [InlineData("<meta property=\"og:title\" content=a&amp;b>", "a&b")]
    [InlineData("<!--> <meta property=\"og:title\" content=\"after empty comment\"> -->", "after empty comment")]
    [InlineData("<!---> <meta property=\"og:title\" content=\"after dash comment\"> -->", "after dash comment")]
    [InlineData("<!DOCTYPE html><meta property=\"og:title\" content=\"doctype\">", "doctype")]
    [InlineData("<?xml version=\"1.0\"?><meta property=\"og:title\" content=\"pi\">", "pi")]
    [InlineData("<![CDATA[ <meta property=\"og:title\" content=\"cdata\"> ]]>", null)]
    [InlineData("<p <meta property=\"og:title\" content=\"broken\">", null)]
    [InlineData("< meta property=\"og:title\" content=\"space\">", null)]
    [InlineData("<meta property=\"og:title\" content=\"tab\there\">", "tab\there")]
    [InlineData("<meta property=\"og:title\" content=\"\0nul\">", null)]
    public void TokenizesLikeLibxml2(string html, string? expected)
    {
        Assert.Equal(expected, Title(html));
    }

    [Fact]
    public void DecodesBytesLikeLibxml2ReadingUtf8()
    {
        Assert.Equal("caf\u00e9 \u00ff x", LegacyHtml.Decode([.. "caf"u8, 0xc3, 0xa9, .. " "u8, 0xff, .. " x"u8]));
        Assert.Equal("\u00FF caf\u00E9 x", LegacyHtml.Decode([0xff, .. " caf"u8, 0xc3, 0xa9, .. " x"u8]));
        Assert.Equal("\u0093q\u0094", LegacyHtml.Decode([0x93, (byte)'q', 0x94]));
        Assert.Equal("\u0082\u00A0", LegacyHtml.Decode([0x82, 0xa0]));
        Assert.Equal("\uD83D\uDE00", LegacyHtml.Decode([0xf0, 0x9f, 0x98, 0x80]));
    }

    [Fact]
    public void FindsTheMetaEncodingLikeNokogiri()
    {
        static string? Encoding(string html) => LegacyHtml.MetaEncoding(LegacyHtml.MetaElements(html));

        Assert.Equal("iso-8859-1", Encoding("<meta charset=\"iso-8859-1\">"));
        Assert.Equal("", Encoding("<meta charset=\"\">"));
        Assert.Equal("iso-8859-1", Encoding("<meta http-equiv=\"content-type\" content=\"text/html; charset=iso-8859-1\">"));
        Assert.Null(Encoding("<meta http-equiv=\"Content-Type\" content=\"text/html\"><meta http-equiv=\"Content-Type\" content=\"charset=utf-8\">"));
        Assert.Null(Encoding("<meta http-equiv=\"refresh\" content=\"charset=utf-8\">"));
        Assert.Null(Encoding("<meta property=\"og:title\" content=\"x\">"));
    }

    /// <summary>Probed against the reference (<c>strip_tags</c> then <c>sanitize</c>).</summary>
    [Theory]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    [InlineData("a < b", "a &lt; b")]
    [InlineData("x&nbsp;y", "x&nbsp;y")]
    [InlineData("\u00A0nb", "&nbsp;nb")]
    [InlineData("Hey!<script>alert('hi')</script>", "Hey!alert('hi')")]
    [InlineData("<!-- c -->t", "t")]
    [InlineData("a &lt;b&gt; c", "a &lt;b&gt; c")]
    [InlineData("<p>one</p><p>two</p>", "onetwo")]
    [InlineData("\"q\" 'a'", "\"q\" 'a'")]
    [InlineData("<style>x</style>y", "xy")]
    [InlineData("&amp;amp;", "&amp;amp;")]
    [InlineData("<b>bold</b>", "bold")]
    [InlineData("</script><img src=a onerror=prompt(1)>", "")]
    [InlineData(" sp  ", " sp  ")]
    [InlineData("<textarea>t<b>x</b></textarea>", "t&lt;b&gt;x&lt;/b&gt;")]
    [InlineData("", "")]
    public void StripsTagsLikeRails(string input, string expected)
    {
        Assert.Equal(expected, OpenGraphMetadata.Sanitize(OpenGraphMetadata.StripTags(input)));
    }

    /// <summary>Pages as large as a fetch allows, built to make a parser do quadratic work, scan in linear time.</summary>
    [Fact]
    public void ScansPathologicalPagesQuickly()
    {
        static string Fill(string open, Func<int, string> item, string close)
        {
            var page = new StringBuilder(open);
            for (var i = 0; page.Length <= OpenGraphFetch.MaxBodySize - close.Length - 64; i++)
            {
                page.Append(item(i));
            }
            return page.Append(close).ToString();
        }

        string[] pages =
        [
            Fill("<meta property=\"og:title\" content=\"x\" ", i => $"a{i:D7} ", ">"),
            Fill("<meta charset=utf-8>", i => $"<meta property=\"og:t{i}\" content=\"x\">", "<meta property=\"og:title\" content=\"x\">"),
            Fill("<meta property=\"og:title\" content=\"", _ => "&amp;\u00E9", "\">"),
        ];
        foreach (var page in pages)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("title", Attributes(page)[0].Key);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"{started.Elapsed} for {page[..60]}");
        }
    }
}
