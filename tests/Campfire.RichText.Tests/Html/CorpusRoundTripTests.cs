using Campfire.RichText.Html;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Html;

// The rich text corpus (vectors/richtext/expected.json) records, for every stored body, the value
// the Lexxy editor receives. On Rails main that value is Nokogiri round trips of the body:
//
//   RichTextHelper#editable_body: Fragment.wrap(body) (parse strip(body)), set content-type and
//     content on each <action-text-attachment>, to_html, loaded as a stored body (canonicalized,
//     so every attachment is emptied)
//   Content#to_editor_html: each attachment replaced by a node rebuilt from its full attributes,
//     to_html
//
// Rendering an attachment and building its attributes is R03's job, so each attachment takes the
// attributes of the reference's own (in document order) and is emptied. Everything else, the parse
// of the body, the tree and every byte of the serialization, has to come out as Nokogiri's.
public sealed class CorpusRoundTripTests
{
    const string attachmentTag = "action-text-attachment";

    [Theory]
    [MemberData(nameof(RichTextVectors.Cases), MemberType = typeof(RichTextVectors))]
    public void Round_trip_matches_the_references_to_html(RichTextCase vector)
    {
        var editable = vector.Editable;
        if (editable.Raised)
        {
            if (editable.Message is HtmlParseException.TreeTooDeep or HtmlParseException.TooManyAttributes)
            {
                // Gumbo's limits: Nokogiri raises ArgumentError while parsing
                Assert.Equal("ArgumentError", editable.Error);
                var error = Assert.Throws<HtmlParseException>(() => HtmlParser.ParseFragment(RubyStrip(vector.Body)));
                Assert.Equal(editable.Message, error.Message);
            }
            else
            {
                // Raised later, rendering an attachment: the parse itself succeeded
                HtmlParser.ParseFragment(RubyStrip(vector.Body)).ToHtml();
            }
            return;
        }

        var expected = editable.Ok;
        var fragment = HtmlParser.ParseFragment(RubyStrip(vector.Body));
        if (expected is not null)
        {
            CopyAttachmentAttributes(HtmlParser.ParseFragment(RubyStrip(expected)), fragment);
        }
        var editableBody = fragment.ToHtml();

        if (expected is null)
        {
            Assert.True(IsBlank(editableBody), $"expected a blank body, got {editableBody}");
            return;
        }
        Assert.Equal(expected, HtmlParser.ParseFragment(RubyStrip(editableBody)).ToHtml());
    }

    [Fact]
    public void The_corpus_covers_attachments_limits_and_plain_bodies()
    {
        var cases = RichTextVectors.File.Cases;
        Assert.True(cases.Count > 600);
        Assert.Contains(cases, c => c.Editable.Ok is { } ok && !ok.Contains(attachmentTag, StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Editable.Ok is { } ok && ok.Contains(attachmentTag, StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Editable.Message == HtmlParseException.TreeTooDeep);
        Assert.Contains(cases, c => c.Editable.Message == HtmlParseException.TooManyAttributes);
    }

    static void CopyAttachmentAttributes(HtmlFragment reference, HtmlFragment fragment)
    {
        // Canonicalizing empties every attachment first, which takes nested ones with it
        foreach (var attachment in Attachments(fragment))
        {
            attachment.RemoveAllChildren();
        }
        var expected = Attachments(reference);
        var actual = Attachments(fragment);
        Assert.True(expected.Count == actual.Count, $"{actual.Count} attachments, the reference has {expected.Count}");
        for (var i = 0; i < actual.Count; i++)
        {
            foreach (var name in actual[i].Attributes.Select(attribute => attribute.Name).ToList())
            {
                actual[i].RemoveAttribute(name);
            }
            foreach (var attribute in expected[i].Attributes)
            {
                actual[i].SetAttribute(attribute.Name, attribute.Value);
            }
        }
    }

    // ActionText's css("action-text-attachment"): by local name, in document order
    static List<HtmlElement> Attachments(HtmlFragment fragment) =>
        fragment.Descendants().OfType<HtmlElement>().Where(e => e.Name == attachmentTag).ToList();

    // String#strip: NUL and ASCII whitespace off both ends
    static string RubyStrip(string value) => value.Trim(['\0', ' ', '\t', '\n', '\v', '\f', '\r']);

    // String#blank?: empty or only whitespace
    static bool IsBlank(string value) => value.All(char.IsWhiteSpace);
}
