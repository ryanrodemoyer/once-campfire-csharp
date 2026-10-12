using System.Text.RegularExpressions;
using Campfire.Web.Helpers;
using static Campfire.Web.Tests.Upstream.U05.Seed;

namespace Campfire.Web.Tests.Upstream.U05;

/// <summary>
/// Message and boost markup at reference edbc779, on the default parity seed: the boost link's
/// <c>data-action</c> (e0e3184), boosts rendered with their message from the preload (fdec7df,
/// b6d3075), token-free quick boost forms (2c53c46, a0d7293, messages/boosts_controller_test.rb),
/// message-id DOM ids (7df98ac), the create response (2c53c46) and copy links that keep the
/// request's port (765711f, messages_controller_test.rb).
/// </summary>
public sealed partial class MessageMarkupTests : IDisposable
{
    // Designers' boosted message, the reference's messages(:first)
    const long boosted = 309456473;

    // EmojiHelper::REACTIONS
    static readonly (string Character, string Title)[] Reactions =
        [("👍", "Thumbs up"), ("👏", "Clapping"), ("👋", "Waving hand"), ("💪", "Muscle"), ("❤️", "Red heart"), ("😂", "Face with tears of joy"), ("🎉", "Party popper"), ("🔥", "Fire")];

    readonly Controllers.Messages.MessagesApp app = App();

    public void Dispose() => app.Dispose();

    Task<Controllers.Messages.Response> GetAsync(string path) => app.SendAsync("GET", path, app.SignedIn(DavidSession, "text/html"));

    [Fact]
    public async Task Index_wires_the_new_boost_link_to_the_soft_keyboard()
    {
        var response = await GetAsync($"/messages/{boosted}/boosts");

        Assert.Equal(200, response.Status);
        Assert.Matches(SoftKeyboardBoostLink(), response.Body);
    }

    // `count_queries { get message_boosts_url(@message) }`. The page's queries are the message's
    // load for messages/_message, so that load is what's counted.
    [Fact]
    public async Task Index_looks_up_the_boosters_of_all_boosts_at_once()
    {
        Boost(boosted, Jason, "🎉", "2026-03-02 15:58:00");
        var withTwoBoosts = await LoadStatementCountAsync();

        var boost = Boost(boosted, Kevin, "👀", "2026-03-02 15:59:00");

        Assert.True(withTwoBoosts > 0);
        Assert.Equal(withTwoBoosts, await LoadStatementCountAsync());
        Assert.Contains($"id=\"boost_{boost}\"", (await GetAsync($"/messages/{boosted}/boosts")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_render_their_preloaded_boosts_in_order()
    {
        var existing = (long)app.Scalar($"SELECT id FROM boosts WHERE message_id = {boosted}")!;
        var earlier = Boost(boosted, Jason, "🥇", "2020-01-01 00:00:00");

        Assert.Equal([earlier, existing], BoostIds((await GetAsync($"/rooms/{Designers}/messages")).Body));
        Assert.Equal([earlier, existing], BoostIds((await GetAsync($"/messages/{boosted}/boosts")).Body));
    }

    [Fact]
    public async Task Quick_boost_controls_submit_their_displayed_reaction_to_the_message()
    {
        var body = (await GetAsync($"/rooms/{Designers}")).Body;
        var actions = body[body.IndexOf($"id=\"message_{boosted}\"", StringComparison.Ordinal)..];
        var forms = QuickBoostForm().Matches(actions[..actions.IndexOf("message__boost-btn", StringComparison.Ordinal)]);

        Assert.Equal(Reactions.Length, forms.Count);
        foreach (var (form, (character, title)) in forms.Zip(Reactions))
        {
            Assert.Equal(
                $"""
                <form data-turbo-frame="boosting_message_{boosted}" data-action="popup#close" action="/messages/{boosted}/boosts" accept-charset="UTF-8" method="post">
                            <input type="hidden" name="boost[content]" id="boost_content" value="{character}" />
                            <button name="button" type="submit" title="{title}" class="btn message__action-btn" data-emoji="{character}">
                """,
                form.Groups[1].Value);
            Assert.DoesNotContain("authenticity_token", form.Value, StringComparison.Ordinal);
        }

        var count = Count("boosts");
        var headers = app.SignedIn(DavidSession, "text/html");
        headers["Sec-Fetch-Site"] = "same-origin";
        var response = await app.SendAsync("POST", $"/messages/{boosted}/boosts", headers, "boost%5Bcontent%5D=%F0%9F%91%8D");

        Assert.Equal(302, response.Status);
        Assert.Equal($"http://campfire.test/messages/{boosted}/boosts", response.Headers.Location.ToString());
        Assert.Equal(count + 1, Count("boosts"));
        Assert.Equal("👍", app.Scalar($"SELECT content FROM boosts WHERE message_id = {boosted} ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task Messages_sharing_a_client_message_id_render_as_distinct_elements()
    {
        var victim = (long)app.Scalar($"SELECT id FROM messages WHERE room_id = {Designers} ORDER BY created_at DESC LIMIT 1")!;
        var clientMessageId = (string)app.Scalar($"SELECT client_message_id FROM messages WHERE id = {victim}")!;
        var colliding = await PostAsync(app, Designers, "Collision", clientMessageId);
        Assert.NotEqual(victim, colliding);

        var ids = MessageId().Matches((await GetAsync($"/rooms/{Designers}/messages")).Body).Select(match => match.Groups[1].Value).ToList();

        Assert.Equal(ids.Distinct(), ids);
        Assert.Contains($"message_{victim}", ids);
        Assert.Contains($"message_{colliding}", ids);
    }

    [Fact]
    public async Task Creating_a_message_answers_with_it_appended()
    {
        var form = "message%5Bbody%5D=New+one&message%5Bclient_message_id%5D=999";

        var response = await app.SendAsync("POST", $"/rooms/{Designers}/messages.turbo_stream", app.SignedIn(DavidSession), form);

        Assert.Equal(200, response.Status);
        Assert.Matches(AppendedBody(), response.Body);
    }

    [Theory]
    [InlineData("once.campfire.test:38683", "http", "http://once.campfire.test:38683")]
    [InlineData("once.campfire.test:8443", "https", "https://once.campfire.test:8443")]
    [InlineData("once.campfire.test:80", "http", "http://once.campfire.test")]
    [InlineData("once.campfire.test:443", "https", "https://once.campfire.test")]
    public async Task Message_copy_links_preserve_the_request_origin_in_broadcasts_and_regular_reads(string host, string scheme, string origin)
    {
        var headers = app.SignedIn(DavidSession);
        headers["Host"] = host;
        headers["Origin"] = origin;
        // The reference test runs without forgery protection. With it on, an HTTPS post needs the
        // fetch metadata browsers send to secure origins (verified_via_header_only? in
        // actionpack's request_forgery_protection.rb); over plain HTTP they send none.
        if (scheme == "https")
        {
            headers["Sec-Fetch-Site"] = "same-origin";
        }
        var form = "message%5Bbody%5D=Request+origin&message%5Bclient_message_id%5D=request-origin";

        var posted = await app.SendAsync("POST", $"/rooms/{Designers}/messages.turbo_stream", headers, form, scheme: scheme);
        Assert.Equal(200, posted.Status);
        var message = (long)app.Scalar($"SELECT MAX(id) FROM messages WHERE room_id = {Designers}")!;
        var copyLink = $"data-copy-to-clipboard-content-value=\\\"{origin}/rooms/{Designers}/@{message}\\\"";

        Assert.Contains(app.Seams.Broadcasts, broadcast => broadcast.Payload.Contains(copyLink, StringComparison.Ordinal));

        headers["Accept"] = "text/html";
        var read = await app.SendAsync("GET", $"/rooms/{Designers}/messages/{message}", headers, scheme: scheme);
        Assert.Equal(200, read.Status);
        Assert.Contains($"data-copy-to-clipboard-content-value=\"{origin}/rooms/{Designers}/@{message}\"", read.Body, StringComparison.Ordinal);
    }

    long Boost(long message, long booster, string content, string at)
    {
        Execute(app, $"INSERT INTO boosts (message_id, booster_id, content, created_at, updated_at) VALUES ({message}, {booster}, '{content}', '{at}', '{at}')");
        return (long)app.Scalar("SELECT MAX(id) FROM boosts")!;
    }

    long Count(string table) => (long)app.Scalar($"SELECT COUNT(*) FROM {table}")!;

    Task<int> LoadStatementCountAsync() =>
        app.Database.ReadAsync(session =>
        {
            var message = Campfire.Data.Queries.Messages.Find(session, boosted)!;
            return Recording(session, () => new MessageViews(app.Keys, body => body).Load(session, [message])).Statements.Count;
        }, Cancellation);

    // `css_select("##{dom_id(message, :boosts)} .boost").map { it["id"] }`
    static List<long> BoostIds(string body)
    {
        var start = body.IndexOf($"id=\"boosts_message_{boosted}\"", StringComparison.Ordinal);
        var container = body[start..body.IndexOf($"id=\"new_boost_message_{boosted}\"", start, StringComparison.Ordinal)];
        return [.. BoostElement().Matches(container).Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
    }

    [GeneratedRegex("""<div class="flex-inline message__boost-inline" data-controller="soft-keyboard">\s*<a class="boost__action txt-small btn" data-action="soft-keyboard#open" """)]
    private static partial Regex SoftKeyboardBoostLink();

    [GeneratedRegex("""(<form data-turbo-frame=[\s\S]*?<button [^>]*>)[\s\S]*?</form>""")]
    private static partial Regex QuickBoostForm();

    [GeneratedRegex("""<div id="(message_\d+)" class="message """)]
    private static partial Regex MessageId();

    [GeneratedRegex("""<div id="boost_(\d+)(?=")""")]
    private static partial Regex BoostElement();

    [GeneratedRegex("""<turbo-stream action="append" target="messages_rooms_closed_\d+"><template>[\s\S]*class="message__body"[\s\S]*New one""")]
    private static partial Regex AppendedBody();
}
