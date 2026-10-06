using Campfire.RichText.Html;
using Campfire.RichText.PlainText;
using Campfire.RichText.Tests.Attachments;

namespace Campfire.RichText.Tests.PlainText;

/// <summary>
/// Action Text's own <c>actiontext/test/unit/plain_text_conversion_test.rb</c> at the pinned Rails
/// revision (1a02651), whose expectations are <c>ActionText::Content.new(html).to_plain_text</c>.
/// </summary>
public class PlainTextConversionTests
{
    [Theory]
    [InlineData("Hello world!\n\nHow are you?", "<p>Hello world!</p><p>How are you?</p>")]
    [InlineData("“Hello world!”\n\n“How are you?”", "<blockquote>Hello world!</blockquote><blockquote>How are you?</blockquote>")]
    [InlineData("   “Hello world!” ", "<blockquote>   Hello world! </blockquote>")]
    [InlineData("“”", "<blockquote> </blockquote>")]
    [InlineData("Hello world!\n\n1. list1\n\n1. list2\n\nHow are you?", "<p>Hello world!</p><ol><li>list1</li></ol><ol><li>list2</li></ol><p>How are you?</p>")]
    [InlineData("Hello world!\n\n• list1\n\n• list2\n\nHow are you?", "<p>Hello world!</p><ul><li>list1</li></ul><ul><li>list2</li></ul><p>How are you?</p>")]
    [InlineData("Hello world!\n\nHow are you?", "<h1>Hello world!</h1><div>How are you?</div>")]
    [InlineData("• one\n• two\n• three", "<ul><li>one</li><li>two</li><li>three</li></ul>")]
    [InlineData("• one\n• two\n• three", "<li>one</li><li>two</li><li>three</li>")]
    [InlineData("• Item 1\n  • Item 2", "<ul><li>Item 1<ul><li>Item 2</li></ul></li></ul>")]
    [InlineData("1. Item 1\n  1. Item 2", "<ol><li>Item 1<ol><li>Item 2</li></ol></li></ol>")]
    [InlineData(
        "• Item 0\n• Item 1\n  • Item A\n    1. Item i\n    2. Item ii\n  • Item B\n    • Item i\n• Item 2",
        "<ul><li>Item 0</li><li>Item 1<ul><li>Item A<ol><li>Item i</li><li>Item ii</li></ol></li><li>Item B<ul><li>Item i</li></ul></li></ul></li><li>Item 2</li></ul>")]
    [InlineData("Hello world!\none\ntwo\nthree", "<p>Hello world!<br>one<br>two<br>three</p>")]
    [InlineData("Hello world!\nHow are you?", "<div>Hello world!</div><div>How are you?</div>")]
    [InlineData("Hello world! [A condor in the mountain]", "Hello world! <figcaption>A condor in the mountain</figcaption>")]
    [InlineData("Hello world! [Cat]", "Hello world! <action-text-attachment url=\"http://example.com/cat.jpg\" content-type=\"image\" caption=\"Cat\"></action-text-attachment>")]
    [InlineData("Hello world!", "<div><strong>Hello </strong>world!</div>")]
    [InlineData("Hello\nHow are you?", "<strong>Hello<br></strong>How are you?")]
    [InlineData("Hello world!", "<script type=\"javascript\">\n  console.log(\"message\");\n</script>\n<div><strong>Hello </strong>world!</div>\n")]
    [InlineData("Hello world!", "<style type=\"text/css\">\n  body { color: red; }\n</style>\n<div><strong>Hello </strong>world!</div>\n")]
    public void Converts_as_action_text_does(string plainText, string html)
    {
        Assert.Equal(plainText, RichTextPlainText.ToPlainText(html, VectorRecords.Context(null)));
    }

    [Fact]
    public void Converts_deeply_nested_tags()
    {
        var fragment = HtmlParser.ParseFragment("<div>Hello world!</div><div></div>");
        var node = (HtmlElement)fragment.Children[^1];
        for (var i = 0; i < 10_000; i++)
        {
            var child = new HtmlElement("div");
            node.AppendChild(child);
            node = child;
        }
        node.AppendChild(new HtmlText("How are you?"));

        Assert.Equal("Hello world!\nHow are you?", PlainTextConversion.NodeToPlainText(fragment));
    }

    // String#chomp(""), as Ruby 3.3 answers
    [Theory]
    [InlineData("a\r\n\n", "a")]
    [InlineData("a\r", "a\r")]
    [InlineData("a\r\r\n", "a\r")]
    [InlineData("a\n\r", "a\n\r")]
    [InlineData("\n\n", "")]
    [InlineData("a b \n", "a b ")]
    public void Removes_trailing_newlines_as_ruby_chomp_does(string text, string expected)
    {
        Assert.Equal(expected, PlainTextConversion.RemoveTrailingNewlines(text));
    }
}
