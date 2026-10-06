using Campfire.RichText.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Attachments;

/// <summary>
/// <c>MessagesHelper#message_presentation</c> against what the reference rendered for every body
/// in <c>vectors/richtext/expected.json</c> (<c>reference-tools/richtext/generate.rb</c>).
/// </summary>
public class PresentationVectorTests
{
    public static TheoryData<RichTextCase> Cases() => RichTextVectors.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Presents_each_body_as_the_reference_does(RichTextCase vector)
    {
        Assert.Equal(Expected(vector), Present(vector));
    }

    [Fact]
    public void Covers_every_reference_case()
    {
        Assert.Equal(658, RichTextVectors.File.Cases.Count);
    }

    [Theory]
    [InlineData("lexxy mention")]
    [InlineData("trix mention")]
    [InlineData("sgid tampered user")]
    [InlineData("sgid expired")]
    [InlineData("sgid cross purpose")]
    public void Mentions_render_the_user_even_when_the_signature_no_longer_verifies(string name)
    {
        var html = PresentedHtml(name);

        Assert.Contains("<span class=\"mention\">", html);
        Assert.Contains("href=\"/users/", html);
    }

    [Fact]
    public void A_tampered_sgid_for_another_model_is_not_a_mention()
    {
        var html = PresentedHtml("sgid tampered room");

        Assert.DoesNotContain("mention", html);
        Assert.Contains("☒", html);
    }

    [Fact]
    public void A_mention_of_a_deleted_user_blanks_the_message_as_rails_does()
    {
        var vector = Case("sgid deleted user");

        Assert.Contains("to_missing_attachable_partial_path", vector.PresentationRaisedMessage);
        Assert.Equal(new Presentation.Html(""), Present(vector));
    }

    // auto_link re-sanitizes with Rails' default allowlist, so figures (and the video inside one) don't reach the screen
    [Theory]
    [InlineData("lexxy embed solo", "og-embed__title")]
    [InlineData("trix tweet avatar", "og-embed--twitter-avatar")]
    [InlineData("content attachment", "<b>there</b>")]
    [InlineData("remote image", "src=\"https://example.com/cat.png\"")]
    [InlineData("attachment gallery", "attachment-gallery--2")]
    public void Renders_each_kind_of_attachment(string name, string marker)
    {
        Assert.Contains(marker, PresentedHtml(name));
    }

    static Presentation Present(RichTextCase vector) =>
        MessagePresentation.Present(vector.Body, VectorRecords.Context(vector.Host));

    static Presentation Expected(RichTextCase vector) =>
        vector.Presentation.Raised ? new Presentation.Unrenderable() : new Presentation.Html(vector.Presentation.Ok!);

    static RichTextCase Case(string name) => RichTextVectors.File.Cases.Single(c => c.Name == name);

    static string PresentedHtml(string name)
    {
        var vector = Case(name);
        var presented = Assert.IsType<Presentation.Html>(Present(vector));
        Assert.Equal(Expected(vector), presented);
        return presented.Value;
    }
}
