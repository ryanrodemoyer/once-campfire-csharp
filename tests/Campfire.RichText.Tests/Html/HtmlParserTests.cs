using Campfire.RichText.Html;

namespace Campfire.RichText.Tests.Html;

// Nokogiri 1.19.4's answers, as reference-rust/crates/richtext/src/dom.rs records them from the
// reference image.
public sealed class HtmlParserTests
{
    static string RoundTrip(string html) => HtmlParser.ParseFragment(html).ToHtml();

    static string? ParseError(string html)
    {
        try
        {
            HtmlParser.ParseFragment(html);
            return null;
        }
        catch (HtmlParseException e)
        {
            return e.Message;
        }
    }

    static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));

    static string NumberedAttributes(int from, int to) =>
        string.Join(' ', Enumerable.Range(from, to - from + 1).Select(i => $"a{i}={i}"));

    [Theory]
    [InlineData("<td>x</td>", "x")]
    [InlineData("<p><table><tr><td>a</td></tr></table>", "<p></p><table><tbody><tr><td>a</td></tr></tbody></table>")]
    [InlineData("<a title='a<b>c' href=\"x&y\u00a0z\">t&lt;\u00a0>\"'</a>", "<a title=\"a<b>c\" href=\"x&amp;y&nbsp;z\">t&lt;&nbsp;&gt;\"'</a>")]
    [InlineData("<pre>\n\nx</pre>", "<pre>\nx</pre>")]
    [InlineData("<noscript><b>x</b></noscript>", "<noscript><b>x</b></noscript>")]
    [InlineData("<?php x ?>", "<!--?php x ?-->")]
    [InlineData("<SVG viewBox='0 0 1 1'><CLIPPATH/></SVG>", "<svg viewBox=\"0 0 1 1\"><clipPath></clipPath></svg>")]
    public void Serializes_like_nokogiri(string html, string expected)
    {
        Assert.Equal(expected, RoundTrip(html));
    }

    [Fact]
    public void Drops_only_a_leading_byte_order_mark()
    {
        Assert.Equal("\ufeffx", RoundTrip("\ufeff\ufeffx"));
        Assert.Equal("<script></script>\ufeffx", RoundTrip("<script></script>\ufeffx"));
    }

    [Fact]
    public void Enforces_gumbos_tree_depth_limit()
    {
        const string tooDeep = HtmlParseException.TreeTooDeep;
        Assert.Null(ParseError(Repeat("<b>", 400)));
        Assert.Equal(tooDeep, ParseError(Repeat("<b>", 401)));
        Assert.Equal(tooDeep, ParseError(Repeat("<b>", 401) + "x"));
        Assert.Equal(tooDeep, ParseError(Repeat("<b>", 400) + "<p>"));
        Assert.Equal(tooDeep, ParseError(Repeat("<b>", 397) + "<table><td>"));
    }

    [Fact]
    public void Counts_open_elements_as_gumbo_does_rather_than_the_final_trees_depth()
    {
        const string tooDeep = HtmlParseException.TreeTooDeep;
        // A void element never goes on the stack of open elements
        Assert.Null(ParseError(Repeat("<b>", 400) + "<br>"));
        Assert.Null(ParseError(Repeat("<b>", 400) + "</p>"));
        // Closing everything again doesn't undo having been too deep
        Assert.Equal(tooDeep, ParseError(Repeat("<b>", 401) + Repeat("</b>", 401)));
        // The adoption agency moves blocks back up, and what counts is how deep they were
        Assert.Null(ParseError(Repeat("<b>" + Repeat("<span>", 300) + Repeat("<div>", 10) + "</b>", 3)));
        Assert.Equal(tooDeep, ParseError("<b>" + Repeat("<span>", 390) + Repeat("<div>", 10) + "</b>" + Repeat("<div>", 300)));
        // Text pending in a table reopens the <b>s past the limit when the input ends, and Gumbo
        // doesn't check after that
        var bs = string.Concat(Enumerable.Range(1, 399).Select(i => $"<b id={i}>"));
        var reopened = $"<p>{bs}</p><div><div><table>x";
        Assert.Null(ParseError(reopened));
        Assert.Equal(tooDeep, ParseError(reopened + "<!---->"));
    }

    [Fact]
    public void Enforces_gumbos_attribute_limit()
    {
        const string tooMany = HtmlParseException.TooManyAttributes;
        Assert.Null(ParseError($"<p {NumberedAttributes(1, 400)}>x</p>"));
        Assert.Equal(tooMany, ParseError($"<p {NumberedAttributes(1, 401)}>x</p>"));
        Assert.Null(ParseError($"<p {string.Join(' ', Enumerable.Repeat("a=1", 1000))}>x</p>"));
        Assert.Null(ParseError($"<textarea><p {NumberedAttributes(1, 401)}>"));
    }

    [Fact]
    public void Counts_attributes_as_gumbos_tokenizer_does()
    {
        const string tooMany = HtmlParseException.TooManyAttributes;
        var attributes = NumberedAttributes(1, 400);
        // Before dropping a duplicate, on end tags, and on a tag the input ends inside
        Assert.Equal(tooMany, ParseError($"<p {attributes} a1=again>x</p>"));
        Assert.Equal(tooMany, ParseError($"<p>x</p {attributes} a401>"));
        Assert.Equal(tooMany, ParseError($"<p {attributes} a401"));
    }

    [Fact]
    public void Keeps_what_the_tokenizer_already_deduplicated()
    {
        Assert.Equal("<p title=\"a\" id=\"c\">x</p>", RoundTrip("<p title=a TITLE=b id=c title=d>x</p>"));
        Assert.Equal(
            "<svg xlink:href=\"a\" href=\"b\" viewBox=\"c\"><a xlink:href=\"d\">x</a></svg>",
            RoundTrip("<svg xlink:href=a href=b viewbox=c><a xlink:href=d>x</a></svg>"));
    }

    [Fact]
    public void Gives_foreign_attributes_their_prefix()
    {
        var svg = Assert.IsType<HtmlElement>(HtmlParser.ParseFragment("<svg xlink:href=a xml:lang=en xmlns:xlink=b>").FirstChild);
        Assert.Equal(HtmlNamespace.Svg, svg.Namespace);
        Assert.Equal(["xlink:href", "xml:lang", "xmlns:xlink"], svg.Attributes.Select(a => a.QualifiedName));
        Assert.Equal(["xlink", "xml", "xmlns"], svg.Attributes.Select(a => a.Prefix));
        Assert.Equal(["href", "lang", "xlink"], svg.Attributes.Select(a => a.Name));
        Assert.Equal("a", svg.GetAttribute("xlink:href"));
    }

    [Fact]
    public void Parses_in_a_nodes_context_like_node_fragment()
    {
        var table = Assert.IsType<HtmlElement>(HtmlParser.ParseFragment("<table></table>").FirstChild);
        var rows = HtmlParser.ParseFragment("<tr><td>x</td></tr>", FragmentContext.For(table));
        Assert.Equal("<tbody><tr><td>x</td></tr></tbody>", rows.ToHtml());

        var inBody = HtmlParser.ParseFragment("<tr><td>x</td></tr>");
        Assert.Equal("x", inBody.ToHtml());
    }

    [Fact]
    public void Serializes_a_document_with_its_doctype()
    {
        var document = HtmlParser.ParseDocument("<!DOCTYPE html><title>t</title><p>x");
        Assert.Equal("<!DOCTYPE html><html><head><title>t</title></head><body><p>x</p></body></html>", document.ToHtml());
        Assert.Equal(HtmlQuirksMode.NoQuirks, document.QuirksMode);
        Assert.Equal(HtmlQuirksMode.Quirks, HtmlParser.ParseDocument("<p>x").QuirksMode);
    }
}
