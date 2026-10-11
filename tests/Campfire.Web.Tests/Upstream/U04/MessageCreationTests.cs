using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.GlobalId;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.Web.Helpers;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Upstream.U04;

/// <summary>
/// Posting a message and what it writes, at reference edbc779: reference/test/models/message/creation_test.rb
/// (7331d3a: the message, its rich text and attachment, its search entry and the unread memberships
/// in one transaction, the push job after it), room_test.rb's unread cases (f36b5a2), message_test.rb's
/// plain text (9b8a168), message/searchable_test.rb (6ad4735) and message/mentionee_test.rb (19c9e6b),
/// on the default parity seed. Designers stands in for the fixtures' rooms: David and Kevin are
/// involved in mentions, Jason and JZ in everything, and Kevin has it unread.
/// </summary>
public sealed class MessageCreationTests : IDisposable
{
    const long designers = 654632876;
    const long allPets = 104393281;
    const long kevinAndBender = 340026324;
    const long david = 127326141;
    const long jason = 149087659;
    const long kevin = 712064548;
    const long bender = 394959859;
    const string davidSession = "AxJs94fteQ5Autv2VrKsH68c";

    readonly MessagesApp messages = new(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));

    public void Dispose() => messages.Dispose();

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creation_indexes_persisted_native_html_and_mentions_before_notifying()
    {
        var html = $"<p>Fish &amp; chips 🌍 {Mention(jason)}</p>";
        var jobs = new IndexAtEnqueue(messages);

        var message = await CreateAsync(designers, david, html, jobs);

        var plainText = await ReadAsync(session => RichTextPlainText.ToPlainText(RichTexts.For(session, Message.ModelName, message.Id, "body")!.Body, Context(session)));
        Assert.Equal(plainText, jobs.IndexedBody);
        Assert.Equal(plainText, IndexedBody(message.Id));
        Assert.StartsWith("Fish & chips 🌍", plainText, StringComparison.Ordinal);
        Assert.DoesNotMatch("<p>|&amp;", IndexedBody(message.Id)!);
        Assert.Equal([message.Id], Search("Fish chips"));
        Assert.Contains(jason, await MentioneesAsync(message));
    }

    [Fact]
    public async Task An_attachment_only_message_is_attached_before_its_filename_is_indexed()
    {
        var body = "--b\r\nContent-Disposition: form-data; name=\"message[attachment]\"; filename=\"lunar.txt\"\r\nContent-Type: text/plain\r\n\r\nhi\r\n" +
            "--b\r\nContent-Disposition: form-data; name=\"message[client_message_id]\"\r\n\r\nmoon\r\n--b--\r\n";

        var response = await messages.SendAsync("POST", $"/rooms/{designers}/messages", messages.SignedIn(davidSession, "*/*"), body, "multipart/form-data; boundary=b");

        Assert.Equal(200, response.Status);
        var id = (long)messages.Scalar("SELECT id FROM messages WHERE client_message_id = 'moon'")!;
        Assert.Equal("lunar.txt", IndexedBody(id));
        Assert.Equal([id], Search("lunar"));
    }

    // 2c53c46: the message is rendered once, and both deliveries, the response and the room's
    // broadcast, carry it.
    [Fact]
    public async Task Posting_renders_the_message_once_for_the_response_and_the_broadcast()
    {
        var response = await messages.SendAsync("POST", $"/rooms/{designers}/messages.turbo_stream", messages.SignedIn(davidSession), "message[body]=New+one&message[client_message_id]=once");

        Assert.Equal(200, response.Status);
        var broadcast = Assert.Single(messages.Seams.Broadcasts, broadcast => broadcast.Stream.EndsWith(":messages", StringComparison.Ordinal));
        Assert.Equal(response.Body.TrimEnd('\n'), System.Text.Json.Nodes.JsonNode.Parse(broadcast.Payload)!.GetValue<string>());
        Assert.StartsWith($"<turbo-stream action=\"append\" target=\"messages_rooms_closed_{designers}\"><template>", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_index_insert_rolls_back_the_message_and_its_associations_without_notifications()
    {
        var before = State();
        messages.Scalar("ALTER TABLE message_search_index RENAME TO message_search_index_away");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync(designers, david, "<p>Failed &amp; indexed</p>", messages.Seams));
        }
        finally
        {
            messages.Scalar("ALTER TABLE message_search_index_away RENAME TO message_search_index");
        }

        Assert.Equal(before, State());
        Assert.Empty(messages.Seams.Jobs);
    }

    [Fact]
    public async Task A_failed_unread_update_rolls_back_the_already_inserted_index_and_associations()
    {
        var before = State();
        messages.Scalar("CREATE TRIGGER unread_unavailable BEFORE UPDATE OF unread_at ON memberships BEGIN SELECT RAISE(ABORT, 'unread unavailable'); END");

        await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync(designers, david, "<p>Unread &amp; rollback</p>", messages.Seams));

        messages.Scalar("DROP TRIGGER unread_unavailable");
        Assert.Equal(before, State());
        Assert.Empty(messages.Seams.Jobs);
    }

    [Fact]
    public async Task Creation_commits_its_index_and_unread_writes_before_enqueuing_push()
    {
        var jobs = new IndexAtEnqueue(messages);

        var message = await CreateAsync(designers, david, "<p>One native commit</p>", jobs);

        var push = Assert.IsType<PushMessageJob>(Assert.Single(jobs.Enqueued));
        Assert.Equal((designers, message.Id), (push.RoomId, push.MessageId));
        Assert.Equal("One native commit", jobs.IndexedBody);
        Assert.Equal(Db(message.CreatedAt), jobs.JasonUnreadAt);
    }

    [Fact]
    public async Task The_existing_public_receive_operation_still_marks_unread_and_queues_a_push()
    {
        var message = await messages.Database.WriteAsync(tx => Messages.Create(tx.Session, designers, david, "received", messages.Now), Cancellation);
        messages.Seams.Clear();

        await messages.Database.WriteAsync(tx =>
        {
            MessageLifecycle.Receive(tx.Session, messages.Seams, Rooms.Find(tx.Session, designers)!, message, messages.Now);
            return true;
        }, Cancellation);

        Assert.Equal(Db(message.CreatedAt), UnreadAt(jason, designers));
        Assert.Equal([new PushMessageJob(designers, message.Id)], messages.Seams.Jobs);
    }

    // room_test.rb, f36b5a2: rewriting every member on every message was most of what posting wrote.
    [Fact]
    public async Task A_new_message_marks_unread_the_members_who_had_read_the_room_and_leaves_the_unread_ones_as_they_were()
    {
        var behind = MembershipRow(kevin, designers);
        Assert.NotNull(UnreadAt(kevin, designers));
        Assert.Null(UnreadAt(jason, designers));

        var message = await CreateAsync(designers, david, "<p>Hello</p>", messages.Seams);

        Assert.Equal(Db(message.CreatedAt), UnreadAt(jason, designers));
        Assert.Equal(behind, MembershipRow(kevin, designers));
        Assert.Null(UnreadAt(david, designers));
    }

    // Directs keep touching them all: a direct's sidebar row is cached by membership and shows the
    // room's recency.
    [Fact]
    public async Task A_direct_marks_its_members_unread_again_with_every_message()
    {
        messages.Scalar($"UPDATE memberships SET unread_at = '2026-03-02 15:00:00', updated_at = '2026-03-02 15:00:00' WHERE room_id = {kevinAndBender} AND user_id = {kevin}");

        var message = await CreateAsync(kevinAndBender, bender, "<p>Status?</p>", messages.Seams);

        Assert.Equal(Db(message.CreatedAt), UnreadAt(kevin, kevinAndBender));
        Assert.Equal(Db(messages.Now), messages.Scalar($"SELECT updated_at FROM memberships WHERE room_id = {kevinAndBender} AND user_id = {kevin}"));
    }

    // message_test.rb, 9b8a168: the native conversion for attachment-free content is the same text.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hello &amp; goodbye")]
    [InlineData("<div>First<br>second</div>")]
    [InlineData("<p>One</p><p>Two</p>")]
    [InlineData("<ul><li>First</li><li><strong>Second</strong></li></ul>")]
    [InlineData("<blockquote>Quoted</blockquote>")]
    [InlineData("<pre><code>one\n  two</code></pre>")]
    [InlineData("<table><tr><td>one</td><td>two</td></tr></table>")]
    [InlineData("<div><em>Unclosed")]
    [InlineData("😄🤘")]
    [InlineData("<div>Hi MENTION</div>")]
    public async Task Plain_text_keeps_action_text_formatting_and_attachment_conversion(string? html)
    {
        html = html?.Replace("MENTION", Mention(david), StringComparison.Ordinal);

        var (plainTextBody, toPlainText) = await ReadAsync(session =>
            (RichTextPlainText.PlainTextBody(html, null, Context(session)), RichTextPlainText.ToPlainText(html, Context(session))));

        Assert.Equal(toPlainText, plainTextBody);
    }

    // searchable_test.rb, 6ad4735: boosts touch their message too, but leave its search entry alone.
    [Theory]
    [InlineData(933434490L)] // a seed message with text
    [InlineData(933434498L)] // a seed message with an attachment
    public async Task Boosting_a_message_leaves_the_search_index_alone(long messageId)
    {
        messages.Scalar($"UPDATE message_search_index SET body = 'untouched' WHERE rowid = {messageId}");
        var message = await ReadAsync(session => Messages.Find(session, messageId)!);

        await messages.Database.WriteAsync(tx =>
        {
            var boost = BoostLifecycle.Create(tx, message, jason, "🦞", messages.Now);
            BoostLifecycle.Destroy(tx, boost, message, messages.Now);
            return true;
        }, Cancellation);

        Assert.Equal("untouched", IndexedBody(messageId));
    }

    [Fact]
    public async Task Saving_a_new_body_replaces_the_old_words_in_the_index()
    {
        var message = await CreateAsync(designers, david, "My hovercraft is full of eels", messages.Seams);

        var response = await Update(message.Id, "message[body]=My+hovercraft+is+full+of+sharks");

        Assert.Equal(302, response.Status);
        Assert.Equal([message.Id], Search("sharks"));
        Assert.Empty(Search("eels"));
    }

    [Fact]
    public async Task Updating_only_the_client_message_id_leaves_the_search_index_alone()
    {
        var message = await CreateAsync(designers, david, "My hovercraft is full of eels", messages.Seams);
        messages.Scalar($"UPDATE message_search_index SET body = 'untouched' WHERE rowid = {message.Id}");

        var response = await Update(message.Id, "message[client_message_id]=renamed");

        Assert.Equal(302, response.Status);
        Assert.Equal("renamed", messages.Scalar($"SELECT client_message_id FROM messages WHERE id = {message.Id}"));
        Assert.Equal("untouched", IndexedBody(message.Id));
    }

    // mentionee_test.rb, 19c9e6b: a message without mentions has no mentionees, without asking its room.
    [Fact]
    public async Task Ordinary_messages_have_no_mentionees_and_deliver_no_webhooks()
    {
        var archive = 699448327L; // Bender, with a webhook, is a member
        var message = await CreateAsync(archive, david, "<p>Fish &amp; chips 🌍</p>", messages.Seams);
        messages.Seams.Clear();

        Assert.Empty(await MentioneesAsync(message));
        await ReadAsync(session =>
        {
            MessageLifecycle.DeliverWebhooksToBots(session, messages.Seams, Rooms.Find(session, archive)!, message, []);
            return true;
        });
        Assert.Empty(messages.Seams.Jobs);
    }

    [Fact]
    public async Task Signed_mentions_follow_a_moved_message_and_fresh_membership_changes()
    {
        var message = await CreateAsync(designers, david, $"<p>Hey {Mention(kevin)}</p>", messages.Seams);
        Assert.Equal([kevin], await MentioneesAsync(message));

        messages.Scalar($"UPDATE messages SET room_id = {allPets} WHERE id = {message.Id}");
        message = await ReadAsync(session => Messages.Find(session, message.Id)!);
        Assert.Empty(await MentioneesAsync(message));

        messages.Scalar($"INSERT INTO memberships (room_id, user_id, created_at, updated_at) VALUES ({allPets}, {kevin}, '2026-03-02 16:00:00', '2026-03-02 16:00:00')");
        Assert.Equal([kevin], await MentioneesAsync(message));

        messages.Scalar($"DELETE FROM memberships WHERE room_id = {allPets} AND user_id = {kevin}");
        Assert.Empty(await MentioneesAsync(message));
        Assert.Equal(allPets, message.RoomId);
    }

    // `room.messages.create!(creator:, body:)`, as a model test makes it.
    Task<Message> CreateAsync(long roomId, long creatorId, string html, IJobQueue jobs) =>
        messages.Database.WriteAsync(tx =>
        {
            var seams = new DomainSeams(messages.Seams, jobs, messages.Seams);
            return MessageLifecycle.Create(tx, seams, roomId, creatorId, null, html, RichTextPlainText.PlainTextBody(html, null, Context(tx.Session)), messages.Now);
        }, Cancellation);

    Task<Response> Update(long messageId, string form) =>
        messages.SendAsync("PATCH", $"/rooms/{designers}/messages/{messageId}", messages.SignedIn(davidSession, "text/html"), form);

    // `message.mentionees.ids`
    Task<List<long>> MentioneesAsync(Message message) => ReadAsync(session =>
    {
        var body = RichTexts.For(session, Message.ModelName, message.Id, "body")?.Body;
        return Users.InRoomWhereIds(session, message.RoomId, MessagePusher.MentionedUserIds(body, Context(session))).Select(user => user.Id).ToList();
    });

    Task<T> ReadAsync<T>(Func<Campfire.Data.Sqlite.SqliteSession, T> work) => messages.Database.ReadAsync(work, Cancellation);

    RenderContext Context(Campfire.Data.Sqlite.SqliteSession session) => new(new DatabaseAttachables(session, messages.Keys, messages.Now), MessagesApp.Host);

    // `mention_attachment_for(name)`
    string Mention(long userId) =>
        $"""<action-text-attachment sgid="{SignedGlobalId.AttachableSgid(messages.Keys, GlobalId.Create("User", userId))}" content-type="application/vnd.campfire.mention"></action-text-attachment>""";

    string? IndexedBody(long messageId) => (string?)messages.Scalar($"SELECT body FROM message_search_index WHERE rowid = {messageId}");

    List<long> Search(string query)
    {
        using var connection = messages.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM message_search_index WHERE body MATCH @query ORDER BY rowid";
        // `match_terms`: each word quoted.
        command.Parameters.AddWithValue("@query", string.Join(' ', query.Split(' ').Select(word => $"\"{word}\"")));
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    string? UnreadAt(long userId, long roomId) => messages.Scalar($"SELECT unread_at FROM memberships WHERE room_id = {roomId} AND user_id = {userId}") as string;

    string MembershipRow(long userId, long roomId) =>
        (string)messages.Scalar($"SELECT unread_at || ' ' || updated_at FROM memberships WHERE room_id = {roomId} AND user_id = {userId}")!;

    // What a rolled-back creation must leave as it was: the rows, the search index, the room's
    // counter and touch, and the memberships.
    string State() => string.Join(" | ",
        messages.Scalar("SELECT COUNT(*) FROM messages"),
        messages.Scalar("SELECT COUNT(*) FROM action_text_rich_texts"),
        messages.Scalar("SELECT COUNT(*) FROM message_search_index"),
        messages.Scalar($"SELECT messages_count || ' ' || updated_at FROM rooms WHERE id = {designers}"),
        messages.Scalar($"SELECT group_concat(user_id || ' ' || ifnull(unread_at, '') || ' ' || updated_at, ', ') FROM (SELECT * FROM memberships WHERE room_id = {designers} ORDER BY id)"));

    static string Db(DateTimeOffset time) => Campfire.RailsCompat.Formatting.ActiveRecordTime.ToDb(time);

    // A job queue that looks at the database, through a connection of its own, when the push job
    // is enqueued: only what has committed shows.
    sealed class IndexAtEnqueue(MessagesApp messages) : IJobQueue
    {
        public List<Job> Enqueued { get; } = [];

        public string? IndexedBody { get; private set; }

        public string? JasonUnreadAt { get; private set; }

        public void Enqueue(Job job)
        {
            Enqueued.Add(job);
            if (job is PushMessageJob push)
            {
                IndexedBody = (string?)messages.Scalar($"SELECT body FROM message_search_index WHERE rowid = {push.MessageId}");
                JasonUnreadAt = messages.Scalar($"SELECT unread_at FROM memberships WHERE room_id = {push.RoomId} AND user_id = {jason}") as string;
            }
        }
    }
}
