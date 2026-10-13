using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;

namespace Campfire.RichText.Tests.PlainText;

/// <summary>
/// An SGID that locates a record other than a user: Message and Room define no
/// <c>attachable_plain_text_representation</c>, so <c>Attachment#to_plain_text</c> is the caption,
/// not the missing attachable's ☒ (found by the Q12 fuzz run, <c>plans/fuzz-run-2.md</c>).
/// </summary>
public class LocatedRecordPlainTextTests
{
    // Expectations from the edbc779 reference: ActionText::Content.new(html).to_plain_text with an
    // attachable SGID of message 1 and of room 1 (Rooms::Open)
    [Theory]
    [InlineData("Message", "1", "a<action-text-attachment sgid=\"e30=--x\"></action-text-attachment>b", "ab")]
    [InlineData("Message", "1", "a<action-text-attachment sgid=\"e30=--x\" caption=\"Hi &lt;b&gt;\"></action-text-attachment>b", "aHi b")]
    [InlineData("Rooms::Open", "1", "a<action-text-attachment sgid=\"e30=--x\"></action-text-attachment>b", "ab")]
    [InlineData("Rooms::Open", "1", "a<action-text-attachment sgid=\"e30=--x\" caption=\"Hi &lt;b&gt;\"></action-text-attachment>b", "aHi b")]
    public void Is_the_caption(string modelName, string modelId, string body, string expected)
    {
        var context = new RenderContext(new Located(modelName, modelId), null);

        Assert.Equal(expected, RichTextPlainText.ToPlainText(body, context));
    }

    sealed class Located(string modelName, string modelId) : IAttachableResolver
    {
        public SignedLookup LocateSigned(string sgid) => new SignedLookup.Record(modelName, modelId);

        public MentionUser? FindGid(string gid, out GidLookupResult result)
        {
            result = GidLookupResult.NotFound;
            return null;
        }
    }

}
