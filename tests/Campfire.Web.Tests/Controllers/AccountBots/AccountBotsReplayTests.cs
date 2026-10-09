using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.RailsCompat.Cookies;
using Campfire.Vectors;
using Campfire.Web.Helpers.Rails;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.AccountBots;

/// <summary>
/// Replays <c>Vectors/bots.json</c>, what the reference did for chat bot administration
/// (index, new, create, edit, update, delete, key regeneration), through the router,
/// Accounts::BotsController and Accounts::Bots::KeysController on the same database, in the
/// same order, with CSRF protection on.
/// </summary>
public sealed partial class AccountBotsReplayTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/AccountBots/Vectors/bots.json")))!;

    static readonly string[] Tables = ["users", "webhooks", "memberships", "sessions"];

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag"];

    static readonly HashSet<string> RandomColumns = ["token", "password_digest", "bot_token"];

    static readonly HashSet<string> SqliteStampedTables = ["memberships"];

    const string sessionId = "0123456789abcdef0123456789abcdef";

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    [Fact]
    public async Task Each_request_answers_and_changes_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            if (sample["sql"] is { } sql)
            {
                app.Scalar(sql.GetValue<string>());
            }
            app.Seams.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), app.Seams.Events).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(Placeholders(sample["changes"]!.AsObject()), Placeholders(Changes(before, after))).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        // CRUD actions
        Assert.Equal(200, Case("bots index")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(200, Case("bots new")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots create without webhook")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots create with webhook")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(200, Case("bots edit Bender")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots update name")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots update webhook")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots remove webhook")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots regenerate key")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, Case("bots destroy")["response"]!["status"]!.GetValue<int>());

        // Key regeneration changes bot_token
        var regen = Case("bots regenerate key");
        Assert.NotNull(regen["changes"]!["users"]);

        // Webhook URL management
        var withHook = Case("bots create with webhook");
        Assert.NotNull(withHook["changes"]!["webhooks"]);

        var removeHook = Case("bots remove webhook");
        Assert.Null(removeHook["changes"]!["webhooks"]![0]!["row"]);

        // Permissions
        foreach (var memberCase in new[] { "bots index, member", "bots new, member", "bots create, member", "bots edit, member", "bots update, member", "bots regenerate key, member", "bots destroy, member" })
        {
            Assert.Equal(403, Case(memberCase)["response"]!["status"]!.GetValue<int>());
        }

        // Show route doesn't exist
        Assert.Equal(404, Case("bots show")["response"]!["status"]!.GetValue<int>());

        // Missing / inactive bot returns 404
        Assert.Equal(404, Case("bots edit, missing bot")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, Case("bots edit, deactivated bot")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, Case("bots regenerate key, deactivated bot")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, Case("bots destroy already deactivated")["response"]!["status"]!.GetValue<int>());
    }

    static JsonNode Case(string name) => Cases().Single(sample => sample["name"]?.GetValue<string>() == name);

    async Task<Response> SendAsync(JsonNode request)
    {
        var target = request["path"]!.GetValue<string>();
        var context = new DefaultHttpContext();
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Method = request["method"]!.GetValue<string>();
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(request["remote_addr"]!.GetValue<string>());
        context.Request.Headers.Host = MessagesApp.Host;
        foreach (var (header, value) in request["headers"]!.AsObject())
        {
            context.Request.Headers[header] = value!.GetValue<string>();
        }
        context.Request.ContentType = request["content_type"]?.GetValue<string>();
        var bytes = Encoding.UTF8.GetBytes(request["body"]?.GetValue<string>() ?? "");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await app.App.HandleAsync(context);
        return new Response(context.Response.StatusCode, context.Response.Headers, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    IEnumerable<string> CompareResponse(JsonNode expected, Response actual)
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
            if (name == "etag" && wantBody != body)
            {
                continue;
            }
            var want = headers[name]?.GetValue<string>();
            var have = actual.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
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
        if (wantBody != haveBody)
        {
            yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
        }
    }

    static string Normalize(string text) =>
        AvatarPath().Replace(
            BotKeyUrl().Replace(
                CsrfMeta().Replace(AuthenticityToken().Replace(text, "$1<token>\""), "$1<token>\""),
                "$1-<token>$2"),
            "/users/<avatar>/avatar");

    static bool SameHeader(string name, string? want, string? have)
    {
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

    JsonObject? SessionData(string setCookie)
    {
        var data = CookieJar.FromHeader(setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)], app.Keys, () => app.Now).Encrypted.Get("_campfire_session") as JsonObject;
        if (data?["session_id"] is { } id && id.GetValue<string>() != sessionId)
        {
            data["session_id"] = "<new session>";
            if (data["_csrf_token"] is not null)
            {
                data["_csrf_token"] = "<new token>";
            }
        }
        return data;
    }

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    static string Attributes(string setCookie) => setCookie[setCookie.IndexOf(';', StringComparison.Ordinal)..];

    static IEnumerable<string> CompareEvents(JsonArray expected, IReadOnlyList<SeamEvent> actual)
    {
        var want = expected.Select(node => node!["broadcast"] is { } stream
            ? $"broadcast {stream.GetValue<string>()} {node["payload"]!.GetValue<string>()}"
            : $"job {node["job"]!.GetValue<string>()} {string.Join(',', node["arguments"]!.AsArray().Select(argument => GidId().Match(argument!.GetValue<string>()).Groups[1].Value))}").ToList();
        var have = actual.Select(seamEvent => seamEvent switch
        {
            Broadcast broadcast => $"broadcast {broadcast.Stream} {broadcast.Payload}",
            Enqueued enqueued => $"job {enqueued.Job.ClassName} {string.Join(',', enqueued.Job.ArgumentIds)}",
            Disconnect disconnect => $"broadcast action_cable/{RecordIdentifier.GidParam("User", disconnect.UserId)} {{\"type\":\"disconnect\",\"reconnect\":{(disconnect.Reconnect ? "true" : "false")}}}",
            _ => seamEvent.ToString(),
        }).ToList();
        if (!want.SequenceEqual(have))
        {
            yield return $"events [{string.Join(", ", have)}], expected [{string.Join(", ", want)}]";
        }
    }

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual)
    {
        var want = expected.ToJsonString(Unescaped);
        var have = actual.ToJsonString(Unescaped);
        if (want != have)
        {
            yield return $"changes {have}\n  expected {want}";
        }
    }

    static JsonObject Placeholders(JsonObject changes)
    {
        changes = (JsonObject)changes.DeepClone();
        foreach (var (table, rows) in changes)
        {
            foreach (var change in rows!.AsArray())
            {
                if (change!["row"] is JsonObject row)
                {
                    IEnumerable<string> random = SqliteStampedTables.Contains(table) ? [.. RandomColumns, "created_at", "updated_at"] : RandomColumns;
                    foreach (var column in random.Where(column => row[column] is not null))
                    {
                        row[column] = "<random>";
                    }
                }
            }
        }
        return changes;
    }

    Dictionary<string, Dictionary<string, JsonObject>> Snapshot()
    {
        using var connection = app.Open();
        var state = new Dictionary<string, Dictionary<string, JsonObject>>();
        foreach (var table in Tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table}";
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

    public void Dispose() => app.Dispose();

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("/([0-9]+)-[A-Za-z0-9]{12}(/messages)")]
    private static partial Regex BotKeyUrl();

    [GeneratedRegex("/users/eyJfcmFpbHMi[A-Za-z0-9_\\-]+--[0-9a-f]{64}/avatar")]
    private static partial Regex AvatarPath();

    [GeneratedRegex("/([0-9]+)$")]
    private static partial Regex GidId();
}
