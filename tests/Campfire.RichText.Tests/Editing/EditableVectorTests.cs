using Campfire.RichText.Editing;
using Campfire.RichText.Html;
using Campfire.RichText.Tests.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Editing;

/// <summary>
/// The <c>&lt;lexxy-editor&gt;</c> value against what the reference built for every body in
/// <c>vectors/richtext/expected.json</c>: <c>render_custom_attachments_in(editable_body(message))</c>.
/// </summary>
public class EditableVectorTests
{
    public static TheoryData<RichTextCase> Cases() => RichTextVectors.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Builds_each_editor_value_as_the_reference_does(RichTextCase vector)
    {
        var context = VectorRecords.Context(vector.Host);
        if (vector.Editable.Raised)
        {
            Assert.ThrowsAny<Exception>(() => EditableContent.EditorValue(vector.Body, context));
        }
        else
        {
            Assert.Equal(vector.Editable.Ok, EditableContent.EditorValue(vector.Body, context));
        }
    }

    [Theory]
    [InlineData("trix mention")]
    [InlineData("trix mention octet stream")]
    [InlineData("trix mention with stale content")]
    [InlineData("sgid tampered user")]
    [InlineData("sgid expired")]
    public void Trix_era_and_unverifiable_mentions_reach_the_editor_as_lexxy_mentions(string name)
    {
        var node = EditorAttachment(name);

        Assert.Equal("application/vnd.campfire.mention", node.GetAttribute("content-type"));
        Assert.StartsWith("\"\\u003cspan class=\\\"mention\\\"", node.GetAttribute("content"));
    }

    [Theory]
    [InlineData("trix unfurl basecamp solo")]
    [InlineData("trix tweet avatar")]
    [InlineData("lexxy hand written embed")]
    public void Embeds_reach_the_editor_with_their_rendered_figure(string name)
    {
        var node = EditorAttachment(name);

        Assert.Equal("application/vnd.actiontext.opengraph-embed", node.GetAttribute("content-type"));
        Assert.Contains("og-embed__title", node.GetAttribute("content"));
    }

    // Ported from reference/test/helpers/rich_text_helper_test.rb. Message.create! stores the
    // body canonicalized, as EditableContent.StoredBody does.

    [Fact]
    public void Editable_body_renders_legacy_opengraph_embeds_into_the_content_attribute()
    {
        var body = """<div>https://example.com/ <action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="https://example.com/image.png" href="https://example.com/" filename="Example title" caption="Example description"></action-text-attachment></div>""";

        var content = HtmlParser.ParseFragment(EditableAttachment(body).GetAttribute("content")!);

        var title = Element(content, e => HasClass(e, "og-embed__title"));
        var link = Element(title, e => e.IsHtml("a"));
        Assert.Equal("Example title", link.TextContent.Trim());
        Assert.Equal("https://example.com/", link.GetAttribute("href"));
        Assert.Equal("Example description", Element(content, e => HasClass(e, "og-embed__description")).TextContent.Trim());
        Assert.Equal("https://example.com/image.png", Element(Element(content, e => HasClass(e, "og-embed__image")), e => e.IsHtml("img")).GetAttribute("src"));
    }

    [Fact]
    public void Editable_body_rebuilds_a_hand_written_embed_from_its_validated_details()
    {
        var embed = """<actiontext-opengraph-embed data-controller="pwn" data-action="click->pwn#run"> <div class="og-embed"><div class="og-embed__title"><a href="/rooms/1">Free cookies</a></div> <div class="og-embed__image"><img src="/rooms/1/avatar" data-action="load->pwn#run"></div></div> </actiontext-opengraph-embed>""";
        var body = $"""<p><action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="https://example.com/image.png" content="{Campfire.RailsCompat.Ruby.RubyEscape.HtmlEscape(embed)}"></action-text-attachment></p>""";

        var node = EditableAttachment(body);
        var rebuilt = HtmlParser.ParseFragment(node.GetAttribute("content")!);

        Assert.Equal("Free cookies", Element(rebuilt, e => HasClass(e, "og-embed__title")).TextContent.Trim());
        Assert.DoesNotContain("rooms/1", node.GetAttribute("content"));
        Assert.DoesNotContain("data-", node.GetAttribute("content"));
        Assert.DoesNotContain(rebuilt.Descendants().OfType<HtmlElement>(), e => e.IsHtml("a") || e.IsHtml("img"));
    }

    [Fact]
    public void Editable_body_restores_the_content_type_of_a_mention_edited_under_trix()
    {
        var david = RichTextVectors.File.Users.Single(u => u.Key == "david");
        var body = $"""<div>Hey <action-text-attachment sgid="{david.AttachableSgid}" content-type="application/octet-stream"></action-text-attachment></div>""";

        var node = EditableAttachment(body);

        Assert.Equal("application/vnd.campfire.mention", node.GetAttribute("content-type"));
        Assert.Contains("David", node.GetAttribute("content"));
    }

    [Fact]
    public void Editable_body_leaves_bodies_without_attachments_unchanged()
    {
        var stored = EditableContent.StoredBody("<p>Plain text</p>");

        Assert.Equal(stored, EditableContent.StoredBody(EditableContent.EditableBody(stored, VectorRecords.Context(null))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t ")]
    public void A_blank_body_gives_the_editor_no_value(string? body)
    {
        Assert.Null(EditableContent.EditorValue(body, VectorRecords.Context(null)));
    }

    static HtmlElement EditorAttachment(string name)
    {
        var vector = RichTextVectors.File.Cases.Single(c => c.Name == name);
        var value = EditableContent.EditorValue(vector.Body, VectorRecords.Context(vector.Host));
        Assert.Equal(vector.Editable.Ok, value);
        return Element(HtmlParser.ParseFragment(value!), e => e.Name == "action-text-attachment");
    }

    static HtmlElement EditableAttachment(string body)
    {
        var context = VectorRecords.Context(RichTextVectors.File.RequestHost);
        var editable = EditableContent.EditableBody(EditableContent.StoredBody(body), context);
        return Element(HtmlParser.ParseFragment(editable), e => e.Name == "action-text-attachment");
    }

    static HtmlElement Element(HtmlParentNode root, Func<HtmlElement, bool> predicate) =>
        root.Descendants().OfType<HtmlElement>().First(predicate);

    static bool HasClass(HtmlElement element, string name) =>
        (element.GetAttribute("class") ?? "").Split(' ').Contains(name);
}
