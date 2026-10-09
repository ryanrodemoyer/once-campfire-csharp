using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Cookies;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.Pwa;

/// <summary>
/// Replays <c>Vectors/pwa.json</c>, what the reference answered for PwaController,
/// QrCodeController, Rails::HealthController and Autocompletable::UsersController
/// (see <c>Vectors/generate.rb</c>), through the router on the same database, in the same
/// order. Each request must answer as the reference did: status, headers, cookies, body
/// and database changes, with only CSRF tokens normalized.
/// </summary>
public sealed partial class PwaControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Pwa/Vectors/pwa.json")))!;

    static readonly string[] Tables = ["users", "sessions"];

    static readonly string[] ComparedHeaders =
    [
        "content-type", "location", "vary", "cache-control", "x-frame-options", "etag",
        "link", "x-total-count", "x-version", "x-rev", "date",
    ];

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

        var manifest = cases["manifest json"];
        Assert.Equal(200, manifest["status"]!.GetValue<int>());
        Assert.Contains("37signals", manifest["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("size=small&amp;v=", manifest["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("application/json; charset=utf-8", manifest["headers"]!["content-type"]!.GetValue<string>());

        var worker = cases["service worker js"];
        Assert.Equal(200, worker["status"]!.GetValue<int>());
        Assert.Equal(870, worker["body"]!.GetValue<string>().Length);
        Assert.StartsWith("text/javascript;", worker["headers"]!["content-type"]!.GetValue<string>(), StringComparison.Ordinal);

        var up = cases["up"];
        Assert.Equal(200, up["status"]!.GetValue<int>());
        Assert.Equal("<!DOCTYPE html><html><body style=\"background-color: green\"></body></html>", up["body"]!.GetValue<string>());
        Assert.False(up["headers"]!.AsObject().ContainsKey("x-version"));
        Assert.Equal(up["body"]!.GetValue<string>(), cases["up old chrome"]["body"]!.GetValue<string>());

        var upJson = cases["up json"];
        Assert.Equal("{\"status\":\"up\",\"timestamp\":\"2026-03-02T16:00:00Z\"}", upJson["body"]!.GetValue<string>());

        var qr = cases["qr example"];
        Assert.Equal(200, qr["status"]!.GetValue<int>());
        Assert.Contains("image/svg+xml", qr["headers"]!["content-type"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("max-age=31556952, public", qr["headers"]!["cache-control"]!.GetValue<string>());

        var users = cases["users json escape"];
        Assert.Equal(200, users["status"]!.GetValue<int>());
        Assert.Contains("\\u0026lt;", users["body"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal(404, cases["users json pets kevin"]["status"]!.GetValue<int>());
        Assert.Equal(302, cases["users signed out"]["status"]!.GetValue<int>());
        Assert.Equal("http://campfire.test/session/new", cases["users signed out"]["headers"]!["location"]!.GetValue<string>());

        var page3 = cases["users json page 3"];
        Assert.Equal("[]", page3["body"]!.GetValue<string>());
        Assert.Contains("page=4", page3["headers"]!["link"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(cases["users json page 2"]["headers"]!.AsObject().ContainsKey("link"));

        Assert.Equal(403, cases["users bot json"]["status"]!.GetValue<int>());
        Assert.Equal("text/html", cases["users bot json"]["headers"]!["content-type"]!.GetValue<string>());
        Assert.Equal("0", cases["users filter array"]["headers"]!["x-total-count"]!.GetValue<string>());
        Assert.Equal("[]", cases["users filter array"]["body"]!.GetValue<string>());
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

    public void Dispose() => app.Dispose();

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();

    sealed record Result(int Status, IHeaderDictionary Headers, byte[] BodyBytes)
    {
        public string Body => Encoding.UTF8.GetString(BodyBytes);
    }
}
