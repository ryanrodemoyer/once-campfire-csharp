using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Cookies;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.PushSubscriptions;

/// <summary>
/// Replays <c>Vectors/push_subscriptions.json</c>, what the reference answered for
/// <c>Users::PushSubscriptionsController</c> and the test-notification action
/// (see <c>Vectors/generate.rb</c>), on the same database in the same order.
/// </summary>
public sealed partial class PushSubscriptionsControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(
        VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/PushSubscriptions/Vectors/push_subscriptions.json")))!;

    static readonly string[] Tables = ["push_subscriptions", "sessions"];

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag"];

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly ScriptedResolver resolver = new();
    readonly MessagesApp app;
    readonly WebPushClient push;

    public PushSubscriptionsControllerTests()
    {
        app = new MessagesApp(
            DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
            Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));
        push = PushSubscriptionClients.Open(resolver, app);
        app.App.WebPush = push;
    }

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    [Fact]
    public async Task Each_request_answers_and_changes_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            resolver.Answer = sample["dns"]!.GetValue<string>();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), Changes(before, after)).Select(failure => $"{name}: {failure}"));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        var cases = Cases().ToDictionary(sample => sample["name"]!.GetValue<string>(), sample => sample["response"]!);

        var index = cases["index"];
        Assert.Equal(200, index["status"]!.GetValue<int>());
        var html = index["body"]!.GetValue<string>();
        Assert.Contains("Push Notification Subscriptions", html, StringComparison.Ordinal);
        Assert.Contains("https://fcm.googleapis.com/fcm/send/123", html, StringComparison.Ordinal);
        Assert.Contains("Send test notification", html, StringComparison.Ordinal);
        Assert.Contains("Delete subscription", html, StringComparison.Ordinal);

        Assert.Equal(406, cases["index format json"]["status"]!.GetValue<int>());
        Assert.Equal(302, cases["index signed out"]["status"]!.GetValue<int>());

        Assert.Equal(200, cases["create new subscription"]["status"]!.GetValue<int>());
        Assert.Equal(200, cases["touch chrome subscription"]["status"]!.GetValue<int>());
        Assert.Equal(422, cases["reject non-permitted endpoint"]["status"]!.GetValue<int>());
        Assert.Equal(422, cases["reject private ip"]["status"]!.GetValue<int>());
        Assert.Equal(422, cases["re-register legacy invalid subscription"]["status"]!.GetValue<int>());

        var test = cases["test notification"];
        Assert.Equal(302, test["status"]!.GetValue<int>());
        Assert.Equal("http://campfire.test/users/me/push_subscriptions", test["headers"]!["location"]!.GetValue<string>());

        var destroyed = cases["destroy chrome"];
        Assert.Equal(302, destroyed["status"]!.GetValue<int>());
        Assert.Equal("http://campfire.test/users/me/push_subscriptions", destroyed["headers"]!["location"]!.GetValue<string>());
    }

    async Task<Result> SendAsync(JsonNode request)
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
        return new Result(context.Response.StatusCode, context.Response.Headers, responseBody.ToArray());
    }

    IEnumerable<string> CompareResponse(JsonNode expected, Result actual)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }

        var body = expected["body"]?.GetValue<string>() ?? "";
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

    static bool SameHeader(string name, string? want, string? have)
    {
        if (name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal))
        {
            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }

        return want == have;
    }

    static string Normalize(string text) =>
        CsrfMeta().Replace(AuthenticityToken().Replace(text, "$1<token>\""), "$1<token>\"");

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
        data?.Remove("session_id");
        data?.Remove("_csrf_token");
        return data;
    }

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    static string Attributes(string setCookie) => setCookie[setCookie.IndexOf(';', StringComparison.Ordinal)..];

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

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual)
    {
        var want = expected.ToJsonString(Unescaped);
        var have = actual.ToJsonString(Unescaped);
        if (want != have)
        {
            yield return $"changes {have}\n  expected {want}";
        }
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

    public void Dispose()
    {
        push.Dispose();
        app.Dispose();
    }

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();

    sealed record Result(int Status, IHeaderDictionary Headers, byte[] BodyBytes)
    {
        public string Body => Encoding.UTF8.GetString(BodyBytes);
    }
}
