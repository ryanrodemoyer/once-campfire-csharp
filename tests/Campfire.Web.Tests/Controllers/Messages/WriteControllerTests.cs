using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.RailsCompat.Cookies;
using Campfire.Vectors;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// Replays <c>Vectors/writes.json</c>, what the reference did for each message create, show,
/// edit, update and destroy (see <c>Vectors/generate.rb</c>), through the router and
/// MessagesWriteController on the same database, in the same order, with CSRF protection on.
/// Each request must answer as the reference did (status, headers, cookies, body), send the
/// same broadcasts and jobs in the same order, and change the same rows to the same values.
/// </summary>
public sealed partial class WriteControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/writes.json")))!;

    static readonly string[] Tables = ["messages", "action_text_rich_texts", "rooms", "memberships", "boosts", "sessions", "active_storage_attachments", "message_search_index"];

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag"];

    // Cases whose rendered message differs from the reference for a reason outside this task: the
    // body, its ETag and the broadcast payloads aren't compared; everything else is.
    static readonly Dictionary<string, string> KnownDifferences = new()
    {
        // M02's message_presentation shows a message with no body row as an empty lexxy-content
        // div; Rails raises and rescues it to "" (reported on #43).
        ["member sends no body"] = "#43",
    };

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp messages = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    [Fact]
    public async Task Each_write_answers_and_changes_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            messages.Seams.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            var known = KnownDifferences.ContainsKey(name);
            failures.AddRange(CompareResponse(sample["response"]!, actual, compareBody: !known).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), messages.Seams.Events, comparePayloads: !known).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), Changes(before, after)).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        var names = Cases().Select(sample => sample["name"]!.GetValue<string>()).ToList();
        Assert.Contains("member creates in an open room", names);
        Assert.Contains("member creates in a direct room with a bot", names);
        Assert.Contains("administrator mentions bots", names);
        Assert.Contains("create without a client message id", names);
        Assert.Contains("create in a room the member isn't in", names);
        Assert.Contains("member edits someone else's message", names);
        Assert.Contains("member updates someone else's message", names);
        Assert.Contains("member destroys someone else's message", names);

        // The benchmark's post (reference-rust/bench: a Turbo form submit) answers 200 with one
        // turbo-stream append of the message to its room.
        var post = Cases().Single(sample => sample["name"]!.GetValue<string>() == "member creates in an open room")["response"]!;
        Assert.Equal(200, post["status"]!.GetValue<int>());
        Assert.StartsWith("<turbo-stream action=\"append\" target=\"messages_rooms_open_201306877\"><template>", post["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    Task<Response> SendAsync(JsonNode request) => messages.SendAsync(
        request["method"]!.GetValue<string>(),
        request["path"]!.GetValue<string>(),
        request["headers"]!.AsObject().ToDictionary(header => header.Key, header => header.Value!.GetValue<string>()),
        request["body"]?.GetValue<string>(),
        request["content_type"]?.GetValue<string>());

    IEnumerable<string> CompareResponse(JsonNode expected, Response actual, bool compareBody)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }
        var body = expected["body"]!.GetValue<string>();
        var (wantBody, haveBody) = (Normalize(body), Normalize(actual.Body));
        var headers = expected["headers"]!.AsObject();
        foreach (var name in ComparedHeaders)
        {
            // A page with CSRF tokens (random masks) or a random client message id has a
            // different Rack::ETag digest each time.
            if (name == "etag" && (wantBody != body || !compareBody))
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
        var wantCookies = headers["set-cookie"] switch
        {
            JsonArray array => array.Select(cookie => cookie!.GetValue<string>()).ToList(),
            JsonValue single => [.. single.GetValue<string>().Split('\n')],
            _ => [],
        };
        foreach (var failure in CompareCookies(wantCookies, [.. actual.Headers.SetCookie.Select(cookie => cookie!)]))
        {
            yield return failure;
        }
        if (compareBody && wantBody != haveBody)
        {
            yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
        }
    }

    const string Placeholder = "<uuid>";

    // CSRF tokens are masked with random bytes, and a message created without a client message
    // id gets a random UUID: both read as placeholders. A message's boost forms carry a token only
    // when the request rendered the message itself: Rails serves the `cache [message, ...]`
    // fragment a broadcast rendered (without a session, so without tokens) when there is one, and
    // fragment caching isn't modeled (M02's known gap), so those tokens are left out on both sides.
    static string Normalize(string text) =>
        Uuid().Replace(CsrfMeta().Replace(AuthenticityToken().Replace(BoostFormToken().Replace(text, "$1"), "$1<token>\""), "$1<token>\""), Placeholder);

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

    IEnumerable<string> CompareCookies(List<string> want, List<string> have)
    {
        var wantNames = want.Select(CookieName).Order().ToList();
        var haveNames = have.Select(CookieName).Order().ToList();
        if (!wantNames.SequenceEqual(haveNames))
        {
            yield return $"cookies [{string.Join(", ", haveNames)}], expected [{string.Join(", ", wantNames)}]";
            yield break;
        }
        foreach (var expected in want)
        {
            var name = CookieName(expected);
            var actual = have.Single(cookie => CookieName(cookie) == name);
            if (name == "_campfire_session")
            {
                // Encrypted with a random IV: compare what it holds, and its attributes.
                var (wantData, haveData) = (SessionData(expected), SessionData(actual));
                if (!JsonNode.DeepEquals(wantData, haveData))
                {
                    yield return $"session {haveData?.ToJsonString()}, expected {wantData?.ToJsonString()}";
                }
                if (Attributes(expected) != Attributes(actual))
                {
                    yield return $"session cookie attributes {Attributes(actual)}, expected {Attributes(expected)}";
                }
            }
            else if (expected != actual)
            {
                yield return $"cookie {actual}, expected {expected}";
            }
        }
    }

    JsonObject? SessionData(string setCookie) =>
        CookieJar.FromHeader(setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)], messages.Keys, () => messages.Now).Encrypted.Get("_campfire_session") as JsonObject;

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    static string Attributes(string setCookie) => setCookie[setCookie.IndexOf(';', StringComparison.Ordinal)..];

    // Broadcasts and jobs, in order: `broadcast <stream> <payload>` and `job <class> <record ids>`.
    static IEnumerable<string> CompareEvents(JsonArray expected, IReadOnlyList<SeamEvent> actual, bool comparePayloads)
    {
        var want = expected.Select(node => node!["broadcast"] is { } stream
            ? $"broadcast {stream.GetValue<string>()} {(comparePayloads ? Normalize(node["payload"]!.GetValue<string>()) : "")}"
            : $"job {node["job"]!.GetValue<string>()} {string.Join(',', node["arguments"]!.AsArray().Select(argument => GidId().Match(argument!.GetValue<string>()).Groups[1].Value))}").ToList();
        var have = actual.Select(seamEvent => seamEvent switch
        {
            Broadcast broadcast => $"broadcast {broadcast.Stream} {(comparePayloads ? Normalize(broadcast.Payload) : "")}",
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

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual)
    {
        var want = Normalize(expected.ToJsonString(Unescaped));
        var have = Normalize(actual.ToJsonString(Unescaped));
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

    [GeneratedRegex("(/boosts\" accept-charset=\"UTF-8\" method=\"post\">)<input type=\"hidden\" name=\"authenticity_token\" value=\"[^\"]*\" />")]
    private static partial Regex BoostFormToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex("/([0-9]+)$")]
    private static partial Regex GidId();
}
