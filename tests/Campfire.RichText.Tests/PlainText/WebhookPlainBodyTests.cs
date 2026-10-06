using Campfire.RichText.PlainText;
using Campfire.RichText.Tests.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.PlainText;

/// <summary>
/// The webhook payload's <c>body.plain</c>: <c>without_recipient_mentions(message.plain_text_body)</c>
/// (<c>reference/app/models/webhook.rb</c>).
/// </summary>
public class WebhookPlainBodyTests
{
    // Expectations from Ruby 3.3 running webhook.rb's method body:
    //   body.gsub("@#{name}", "").gsub(/\A\p{Space}+|\p{Space}+\z/, "")
    [Theory]
    [InlineData("Hey @David how are you?", "David", "Hey  how are you?")]
    [InlineData("@Bender hi @Bender", "Bender", "hi")]
    [InlineData("\u00a0\u2003 @Bender  hello\n\u3000", "Bender", "hello")]
    [InlineData("@Benderino hi", "Bender", "ino hi")]
    [InlineData("\u200bhi\u180e", "Bender", "\u200bhi\u180e")]
    [InlineData("\u0085\u2028hi\u2029\u205f", "Bender", "hi")]
    [InlineData("@Mallory & \"Evil\" ping", "Mallory <b>&amp;</b> \"Evil\"", "@Mallory & \"Evil\" ping")]
    [InlineData("@Mallory & \"Evil\" ping", "Mallory & \"Evil\"", "ping")]
    [InlineData("hi @", "", "hi")]
    [InlineData("@Seán O'Brien 🎉 thanks", "Seán O'Brien 🎉", "thanks")]
    public void Removes_the_recipients_mentions_and_surrounding_unicode_space(string plainText, string recipientName, string expected)
    {
        Assert.Equal(expected, RichTextPlainText.WithoutRecipientMentions(plainText, recipientName));
    }

    [Theory]
    [InlineData("lexxy mention", "jason", "Hey @David how are you?")]
    [InlineData("lexxy mention", "david", "Hey  how are you?")]
    [InlineData("lexxy two mentions", "david", "and @Jason and")]
    [InlineData("trix mention", "david", "Hey")]
    [InlineData("lexxy mention quote name", "obrien", "")]
    // The plain text holds the name as its markup parses, so a name with markup in it never matches
    [InlineData("lexxy mention html name", "mallory", "Hi @Mallory & \"Evil\"")]
    public void Strips_the_bot_from_the_reference_plain_text_of_a_message_mentioning_it(string name, string recipient, string expected)
    {
        var vector = RichTextVectors.File.Cases.Single(c => c.Name == name);
        var bot = RichTextVectors.File.Users.Single(u => u.Key == recipient);
        var context = VectorRecords.Context(vector.Host);

        var plainTextBody = RichTextPlainText.PlainTextBody(vector.Body, null, context);

        Assert.Equal(vector.PlainText.Ok, plainTextBody);
        Assert.Equal(expected, RichTextPlainText.WithoutRecipientMentions(plainTextBody, bot.Name));
    }
}
