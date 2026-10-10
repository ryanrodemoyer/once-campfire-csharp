namespace Campfire.Web.Tests.Controllers.Rooms;

/// <summary>
/// A stored body whose plain text raises must blank that one message, not the room page.
/// <c>message_tag</c> reads <c>plain_text_body</c> for the emoji class and rescues
/// <c>Exception</c> (<c>reference/app/helpers/messages_helper.rb</c>). Any member can store such a
/// body: indexing happens after commit, so a later show is what fails. CSRF protection is on.
/// </summary>
public sealed class RaisingPlainTextRoomTests : IDisposable
{
    const string david = "AxJs94fteQ5Autv2VrKsH68c";
    const long allTalk = 486777696;

    // Invalid base64: Action Text raises ArgumentError, which message_tag logs and replaces
    // with messages/_unrenderable. The row is inserted directly because create computes plain
    // text before the insert.
    readonly Messages.MessagesApp app = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero),
    [
        """
        INSERT INTO messages (id, client_message_id, created_at, creator_id, room_id, updated_at)
        VALUES (933434700, 'plain-text-raises', '2026-03-02 15:59:00', 127326141, 486777696, '2026-03-02 15:59:00')
        """,
        """
        INSERT INTO action_text_rich_texts (id, body, created_at, name, record_id, record_type, updated_at)
        VALUES (933434700, '<p><action-text-attachment sgid="!!!--abc"></action-text-attachment></p>', '2026-03-02 15:59:00', 'body', 933434700, 'Message', '2026-03-02 15:59:00')
        """,
    ]);

    [Fact]
    public async Task Show_renders_one_unrenderable_message_when_its_plain_text_raises()
    {
        var response = await app.SendAsync("GET", $"/rooms/{allTalk}", app.SignedIn(david, "text/html"));

        Assert.Equal(200, response.Status);
        Assert.Contains("Failed to load message content", response.Body, StringComparison.Ordinal);
        Assert.Contains("When we are not sure, we are alive.", response.Body, StringComparison.Ordinal);
    }

    public void Dispose() => app.Dispose();
}
