using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Jobs.Webhooks;
using Campfire.Vectors;
using Campfire.Web.Controllers;
using Campfire.Web.Helpers;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// Replays <c>Vectors/by_bots.json</c>, what the reference did for each request to the bot API
/// (see <c>Vectors/by_bots.rb</c>), through the router and the ByBots controllers on the same
/// database, in the same order, with CSRF protection on. Each request must answer as the reference
/// did (status, headers, body), send the same broadcasts and jobs in the same order, and change
/// the same rows to the same values.
/// </summary>
public sealed partial class ByBotsReplayTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/by_bots.json")))!;

    static readonly string[] Tables = ["messages", "rooms", "memberships", "boosts", "action_text_rich_texts", "message_search_index"];

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag", "x-total-count", "link"];

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp messages = new(DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));

    // Messages the bots create get a random client_message_id on each side: each reads as the id
    // of its message, which both sides number alike.
    readonly Dictionary<string, string> wantMessageIds = [];
    readonly Dictionary<string, string> haveMessageIds = [];

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    [Fact]
    public async Task Each_request_answers_and_changes_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            messages.Seams.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();
            var changes = Changes(before, after);
            NameNewMessages(sample["changes"]!.AsObject(), wantMessageIds);
            NameNewMessages(changes, haveMessageIds);

            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), messages.Seams.Events).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), changes).Select(failure => $"{name}: {failure}"));
        }

        // Then, on the database the requests left, Bot::WebhookJob's halves for Bender's webhook:
        // the payload it posts for a message, and what a text reply makes.
        using var client = new WebhookClient();
        var replies = new ByBotsWebhookReplies(messages.App);
        var webhooks = new BotWebhooks(messages.Database, client, replies, session => new DatabaseAttachables(session, messages.Keys, messages.Now));
        var bender = await messages.Database.ReadAsync(session => Users.Find(session, 394959859)!, TestContext.Current.CancellationToken);
        foreach (var sample in Vectors["webhook_payloads"]!.AsArray())
        {
            var name = sample!["name"]!.GetValue<string>();
            var payload = await messages.Database.ReadAsync(session =>
            {
                var message = Data.Queries.Messages.Find(session, sample["message_id"]!.GetValue<long>())!;
                return webhooks.Payload(session, bender, Data.Queries.Rooms.Find(session, message.RoomId)!, message);
            }, TestContext.Current.CancellationToken);
            if (payload != sample["payload"]!.GetValue<string>())
            {
                failures.Add($"payload for {name}: {FirstDifference(sample["payload"]!.GetValue<string>(), payload)}");
            }
        }
        foreach (var sample in Vectors["webhook_replies"]!.AsArray())
        {
            var name = sample!["name"]!.GetValue<string>();
            messages.Seams.Clear();
            var room = await messages.Database.ReadAsync(session => Data.Queries.Rooms.Find(session, sample["room_id"]!.GetValue<long>())!, TestContext.Current.CancellationToken);
            var before = Snapshot();
            await replies.ReceiveTextAsync(room, bender, new WebhookTextReply(sample["text"]!.GetValue<string>()), TestContext.Current.CancellationToken);
            var changes = Changes(before, Snapshot());
            NameNewMessages(sample["changes"]!.AsObject(), wantMessageIds);
            NameNewMessages(changes, haveMessageIds);

            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), messages.Seams.Events).Select(failure => $"{name} reply: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), changes).Select(failure => $"{name} reply: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        var cases = Cases().ToDictionary(sample => sample["name"]!.GetValue<string>());
        // Pagination headers, both ways.
        Assert.Contains("rel=\"next\"", cases["index, the last page"]!["response"]!["headers"]!["link"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("?after=", cases["index after a message"]!["response"]!["headers"]!["link"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("131", cases["index, the last page"]!["response"]!["headers"]!["x-total-count"]!.GetValue<string>());
        // Raw bodies under each Content-Type, a status and Location.
        Assert.Equal(201, cases["create as JSON"]!["response"]!["status"]!.GetValue<int>());
        Assert.NotNull(cases["create"]!["response"]!["headers"]!["location"]);
        Assert.Equal(422, cases["create with a blank body"]!["response"]!["status"]!.GetValue<int>());
        // A mention of a bot with a webhook enqueues its delivery; the sender's own doesn't.
        Assert.Contains(cases["create mentioning a bot with a webhook"]!["events"]!.AsArray(), node => node!["job"]?.GetValue<string>() == "Bot::WebhookJob");
        Assert.DoesNotContain(cases["create mentioning itself"]!["events"]!.AsArray(), node => node!["job"]?.GetValue<string>() == "Bot::WebhookJob");
        Assert.Equal(201, cases["boost"]!["response"]!["status"]!.GetValue<int>());
        Assert.Equal(204, cases["unboost"]!["response"]!["status"]!.GetValue<int>());
    }

    Task<Response> SendAsync(JsonNode request) => messages.SendAsync(
        request["method"]!.GetValue<string>(),
        request["path"]!.GetValue<string>(),
        request["headers"]!.AsObject().ToDictionary(header => header.Key, header => header.Value!.GetValue<string>()),
        request["body"]?.GetValue<string>(),
        request["content_type"]?.GetValue<string>());

    IEnumerable<string> CompareResponse(JsonNode expected, Response actual)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }
        var body = expected["body"]!.GetValue<string>();
        var (wantBody, haveBody) = (Normalize(body, wantMessageIds), Normalize(actual.Body, haveMessageIds));
        var headers = expected["headers"]!.AsObject();
        foreach (var name in ComparedHeaders)
        {
            // A page with CSRF tokens (random masks) has a different Rack::ETag digest each time.
            if (name == "etag" && wantBody != body)
            {
                continue;
            }
            var want = headers[name]?.GetValue<string>();
            var have = actual.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
            // The generator calls Rails.application, inside config.ru's Rack::Deflater.
            if (name == "vary" && have is not null)
            {
                have = string.Join(',', have.Split(',').Where(part => part != "Accept-Encoding"));
                have = have.Length == 0 ? null : have;
            }
            if (!SameHeader(name, want, have))
            {
                yield return $"{name} {have ?? "(none)"}, expected {want ?? "(none)"}";
            }
        }
        // Signing a bot in never sets a cookie; rendering a form token starts a session, whose
        // random id and token can't compare, so only the cookie's name does.
        var wantCookies = headers["set-cookie"] is { } cookies ? cookies.GetValue<string>().Split('\n').Select(CookieName).Order().ToList() : [];
        var haveCookies = actual.Headers.SetCookie.Select(cookie => CookieName(cookie!)).Order().ToList();
        if (!wantCookies.SequenceEqual(haveCookies))
        {
            yield return $"cookies [{string.Join(", ", haveCookies)}], expected [{string.Join(", ", wantCookies)}]";
        }
        if (wantBody != haveBody)
        {
            yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
        }
    }

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    // CSRF tokens are masked with random bytes, so they read as placeholders, and so do the
    // client_message_ids of the messages the bots made.
    static string Normalize(string text, Dictionary<string, string> messageIds)
    {
        text = AuthenticityToken().Replace(text, "$1<token>\"");
        foreach (var (uuid, name) in messageIds)
        {
            text = text.Replace(uuid, name, StringComparison.Ordinal);
        }
        return text;
    }

    static void NameNewMessages(JsonObject changes, Dictionary<string, string> messageIds)
    {
        foreach (var change in changes["messages"]?.AsArray() ?? [])
        {
            if (change!["row"] is JsonObject row && row["client_message_id"]?.GetValue<string>() is { } uuid && Uuid().IsMatch(uuid))
            {
                messageIds.TryAdd(uuid, $"<client_message_id of {row["id"]}>");
            }
        }
    }

    static bool SameHeader(string name, string? want, string? have)
    {
        // Rails' error pages (PublicExceptions) spell the charset "UTF-8"; W01's ErrorPages
        // writes "utf-8" (reported on #22).
        if (name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal))
        {
            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }
        return want == have;
    }

    // Broadcasts and jobs, in order: `broadcast <stream> <payload>` and `job <class> <record ids>`.
    IEnumerable<string> CompareEvents(JsonArray expected, IReadOnlyList<SeamEvent> actual)
    {
        var want = expected.Select(node => node!["broadcast"] is { } stream
            ? $"broadcast {stream.GetValue<string>()} {Normalize(node["payload"]!.GetValue<string>(), wantMessageIds)}"
            : $"job {node["job"]!.GetValue<string>()} {string.Join(',', node["arguments"]!.AsArray().Select(argument => GidId().Match(argument!.GetValue<string>()).Groups[1].Value))}").ToList();
        var have = actual.Select(seamEvent => seamEvent switch
        {
            Broadcast broadcast => $"broadcast {broadcast.Stream} {Normalize(broadcast.Payload, haveMessageIds)}",
            Enqueued enqueued => $"job {enqueued.Job.ClassName} {string.Join(',', enqueued.Job.ArgumentIds)}",
            _ => seamEvent.ToString(),
        }).ToList();
        if (!want.SequenceEqual(have))
        {
            yield return $"events\n    [{string.Join("\n     ", have.Select(Abbreviate))}]\n  expected\n    [{string.Join("\n     ", want.Select(Abbreviate))}]";
            for (var i = 0; i < Math.Min(want.Count, have.Count); i++)
            {
                if (want[i] != have[i])
                {
                    yield return $"event {i}: {FirstDifference(want[i], have[i])}";
                    break;
                }
            }
        }
    }

    IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual)
    {
        var want = Normalize(expected.ToJsonString(Unescaped), wantMessageIds);
        var have = Normalize(actual.ToJsonString(Unescaped), haveMessageIds);
        if (want != have)
        {
            yield return $"changes {have}\n  expected {want}";
        }
    }

    // Every row of the tables, keyed by its first column, as the generator's `snapshot`.
    Dictionary<string, Dictionary<string, JsonObject>> Snapshot()
    {
        using var connection = messages.Open();
        var state = new Dictionary<string, Dictionary<string, JsonObject>>();
        foreach (var table in Tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = table == "message_search_index" ? "SELECT rowid, body FROM message_search_index" : $"SELECT * FROM {table}";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, JsonObject>();
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                    {
                        long integer => JsonValue.Create(integer),
                        double real => JsonValue.Create(real),
                        string text => JsonValue.Create(text),
                        var other => JsonValue.Create(other.ToString()),
                    };
                }
                rows[row[reader.GetName(0)]!.ToJsonString()] = row;
            }
            state[table] = rows;
        }
        return state;
    }

    // The generator's `changes`: per table, each row that differs, by id, with its new values
    // (null when it's gone).
    static JsonObject Changes(Dictionary<string, Dictionary<string, JsonObject>> before, Dictionary<string, Dictionary<string, JsonObject>> after)
    {
        var changes = new JsonObject();
        foreach (var table in Tables)
        {
            var rows = new JsonArray();
            var ids = before[table].Keys.Union(after[table].Keys).OrderBy(id => long.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
            foreach (var id in ids)
            {
                var old = before[table].GetValueOrDefault(id);
                var current = after[table].GetValueOrDefault(id);
                if (old is not null && current is not null && JsonNode.DeepEquals(old, current))
                {
                    continue;
                }
                rows.Add(new JsonObject { ["id"] = JsonNode.Parse(id), ["row"] = current?.DeepClone() });
            }
            if (rows.Count > 0)
            {
                changes[table] = rows;
            }
        }
        return changes;
    }

    static string FirstDifference(string want, string have)
    {
        var at = 0;
        while (at < want.Length && at < have.Length && want[at] == have[at])
        {
            at++;
        }
        var from = Math.Max(0, at - 120);
        return $"at {at} of {want.Length}/{have.Length}:\n    got      {Excerpt(have, from, at)}\n    expected {Excerpt(want, from, at)}";
    }

    static string Excerpt(string text, int from, int at) =>
        JsonSerializer.Serialize(text[from..Math.Min(text.Length, at + 160)], Unescaped);

    static string Abbreviate(string text) => text.Length > 160 ? text[..160] + "…" : text;

    public void Dispose() => messages.Dispose();

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex Uuid();

    [GeneratedRegex("/([0-9]+)$")]
    private static partial Regex GidId();
}
