using Campfire.RichText.PlainText;
using Campfire.RichText.Tests.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.PlainText;

/// <summary>
/// <c>message.body.to_plain_text</c> against what the reference converted for every body in
/// <c>vectors/richtext/expected.json</c> (<c>reference-tools/richtext/generate.rb</c>).
/// </summary>
public class PlainTextVectorTests
{
    public static TheoryData<RichTextCase> Cases() => RichTextVectors.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Converts_each_body_as_the_reference_does(RichTextCase vector)
    {
        var context = VectorRecords.Context(vector.Host);
        if (vector.PlainText.Raised)
        {
            Assert.ThrowsAny<Exception>(() => RichTextPlainText.ToPlainText(vector.Body, context));
        }
        else
        {
            Assert.Equal(vector.PlainText.Ok, RichTextPlainText.ToPlainText(vector.Body, context));
        }
    }

    [Theory]
    [InlineData("lexxy mention", "Hey @David how are you?")]
    [InlineData("trix mention", "Hey @David")]
    [InlineData("sgid tampered user", "@Jason")]
    [InlineData("lexxy embed solo", "https://basecamp.com/")]
    [InlineData("remote image", "[A cat]")]
    [InlineData("span only (searchable test)", "My hovercraft is full of eels")]
    public void Attachments_convert_to_their_plain_text_representation(string name, string expected)
    {
        var vector = RichTextVectors.File.Cases.Single(c => c.Name == name);
        Assert.Equal(expected, vector.PlainText.Ok);
        Assert.Equal(expected, RichTextPlainText.ToPlainText(vector.Body, VectorRecords.Context(vector.Host)));
    }

    // Message#plain_text_body: body.to_plain_text.presence || attachment&.filename&.to_s || ""
    [Theory]
    [InlineData(null, null, "")]
    [InlineData(null, "moon.jpg", "moon.jpg")]
    [InlineData("", "moon.jpg", "moon.jpg")]
    [InlineData("<p> \u00a0</p>", "moon.jpg", "moon.jpg")]
    [InlineData("<p>Look</p>", "moon.jpg", "Look")]
    [InlineData("<p><br></p>", null, "")]
    public void The_plain_text_body_falls_back_to_the_attachments_filename(string? body, string? filename, string expected)
    {
        Assert.Equal(expected, RichTextPlainText.PlainTextBody(body, filename, VectorRecords.Context(null)));
    }

    [Fact]
    public void Script_contents_never_reach_the_plain_text()
    {
        var plainText = RichTextPlainText.ToPlainText("<p>1 &lt; 2 &amp;&amp; <b>bold</b><script>alert(1)</script></p>", VectorRecords.Context(null));

        Assert.Equal("1 < 2 && bold", plainText);
    }
}
