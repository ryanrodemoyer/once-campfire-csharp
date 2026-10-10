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

namespace Campfire.Web.Tests.Controllers.Sessions;

/// <summary>
/// Replays <c>Vectors/sessions.json</c>, what the reference did for each sign in, sign out, root
/// and first-run request (see <c>Vectors/generate.rb</c>), through the router and the sessions,
/// welcome and first-run controllers on the same database, in the same order, with CSRF
/// protection on. Each request must answer as the reference did (status, headers, cookies, body),
/// send the same broadcasts and change the same rows to the same values. Random values (session
/// tokens, password digests, join codes, CSRF masks, new session ids) read as placeholders.
/// </summary>
public sealed partial class SessionsControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Sessions/Vectors/sessions.json")))!;

    static readonly string[] Tables = ["accounts", "users", "rooms", "memberships", "sessions", "push_subscriptions"];

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options"];

    // Columns holding random values: compared as present or absent. Memberships are stamped by
    // SQLite's CURRENT_TIMESTAMP (insert_all), which the reference's frozen clock stops and ours
    // doesn't.
    static readonly HashSet<string> RandomColumns = ["token", "password_digest", "join_code"];

    static readonly HashSet<string> SqliteStampedTables = ["memberships"];

    static readonly Dictionary<string, string> KnownDifferences = [];

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
            if (sample["reset"] is JsonArray reset)
            {
                foreach (var sql in reset)
                {
                    app.Scalar(sql!.GetValue<string>());
                }
                continue;
            }
            app.Seams.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            var known = KnownDifferences.ContainsKey(name);
            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
            if (!known)
            {
                failures.AddRange(CompareEvents(sample["events"]!.AsArray(), app.Seams.Events).Select(failure => $"{name}: {failure}"));
            }
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), Changes(before, after)).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_benchmark_login()
    {
        // reference-rust/bench's login: GET /session/new, then POST /session with the page's token,
        // which answers 302 to the root with a session_token cookie.
        var page = Case("sign-in page")["response"]!;
        Assert.Equal(200, page["status"]!.GetValue<int>());
        Assert.Contains("name=\"authenticity_token\"", page["body"]!.GetValue<string>(), StringComparison.Ordinal);
        var signIn = Case("sign in");
        Assert.Contains("authenticity_token=", signIn["request"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(302, signIn["response"]!["status"]!.GetValue<int>());
        Assert.Equal("http://campfire.test/", signIn["response"]!["headers"]!["location"]!.GetValue<string>());
        Assert.Contains(Cookies(signIn["response"]!), cookie => cookie.StartsWith("session_token=", StringComparison.Ordinal));
        Assert.Equal(422, Case("sign in, no CSRF token")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(429, Case("rate limit, attempt 11")["response"]!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_benchmark_login_signs_in_with_the_pages_token()
    {
        // As the load generator does it: read the token from the form, post it back with the
        // session cookie the page set.
        var browser = new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
            ["Accept"] = "text/html, application/xhtml+xml",
        };
        var page = await app.SendAsync("GET", "/session/new", browser);
        Assert.Equal(200, page.Status);
        var token = FormToken().Match(page.Body).Groups[1].Value;
        var session = page.Headers.SetCookie.Select(cookie => cookie!).Single(cookie => cookie.StartsWith("_campfire_session=", StringComparison.Ordinal));
        browser["Cookie"] = session[..session.IndexOf(';', StringComparison.Ordinal)];
        var body = $"authenticity_token={Uri.EscapeDataString(token)}&email_address=david%4037signals.com&password=secret123456";
        var signIn = await app.SendAsync("POST", "/session", browser, body);
        Assert.Equal(302, signIn.Status);
        Assert.Equal("http://campfire.test/", signIn.Headers.Location.ToString());
        Assert.Contains(signIn.Headers.SetCookie, cookie => cookie!.StartsWith("session_token=", StringComparison.Ordinal));
    }

    static JsonNode Case(string name) => Cases().Single(sample => sample["name"]?.GetValue<string>() == name);

    static List<string> Cookies(JsonNode response) => response["headers"]!["set-cookie"] switch
    {
        JsonArray array => [.. array.Select(cookie => cookie!.GetValue<string>())],
        JsonValue single => [.. single.GetValue<string>().Split('\n')],
        _ => [],
    };

    // A request through the whole app from the case's remote address.
    async Task<Response> SendAsync(JsonNode request)
    {
        var target = request["path"]!.GetValue<string>();
        var body = request["body"]?.GetValue<string>();
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
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
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
        var headers = expected["headers"]!.AsObject();
        foreach (var name in ComparedHeaders)
        {
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
        var wantCookies = Cookies(expected);
        foreach (var failure in CompareCookies(wantCookies, [.. actual.Headers.SetCookie.Select(cookie => cookie!)]))
        {
            yield return failure;
        }
        var (wantBody, haveBody) = (Normalize(expected["body"]!.GetValue<string>()), Normalize(actual.Body));
        if (wantBody != haveBody)
        {
            yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
        }
    }

    // CSRF tokens are masked with random bytes.
    static string Normalize(string text) => CsrfMeta().Replace(AuthenticityToken().Replace(text, "$1<token>\""), "$1<token>\"");

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
        var wantNames = want.Select(CookieName).ToList();
        var haveNames = have.Select(CookieName).ToList();
        if (!wantNames.Order().SequenceEqual(haveNames.Order()))
        {
            yield return $"cookies [{string.Join(", ", haveNames)}], expected [{string.Join(", ", wantNames)}]";
            yield break;
        }
        foreach (var (expected, actual) in want.OrderBy(CookieName).ThenBy(Value).Zip(have.OrderBy(CookieName).ThenBy(Value)))
        {
            var name = CookieName(expected);
            if (name == "_campfire_session")
            {
                // Encrypted with a random IV: compare what it holds, and its attributes.
                var (wantData, haveData) = (SessionData(expected), SessionData(actual));
                if (!JsonNode.DeepEquals(wantData, haveData))
                {
                    yield return $"session {haveData?.ToJsonString()}, expected {wantData?.ToJsonString()}";
                }
            }
            else if (name == "session_token" && Value(expected).Length > 0)
            {
                // Signs a new session's random token: compare that one is set.
                if (Value(actual).Length == 0)
                {
                    yield return $"cookie {actual}, expected a session token";
                }
            }
            else if (Value(expected) != Value(actual))
            {
                yield return $"cookie {actual}, expected {expected}";
            }
            if (Attributes(expected) != Attributes(actual))
            {
                yield return $"cookie attributes {Attributes(actual)}, expected {Attributes(expected)}";
            }
        }
    }

    // The session's contents, with a new session's random id and CSRF token as placeholders.
    JsonObject? SessionData(string setCookie)
    {
        if (CookieJar.FromHeader(setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)], app.Keys, () => app.Now).Encrypted.Get("_campfire_session") is not JsonObject data)
        {
            return null;
        }
        data = (JsonObject)data.DeepClone();
        if (data["session_id"]?.GetValue<string>() is { } id && id != "0123456789abcdef0123456789abcdef")
        {
            data["session_id"] = "<new>";
        }
        if (data["_csrf_token"]?.GetValue<string>() is { } csrf && csrf != "a01SessionsCsrfToken0a01SessionsCsrfToken0A")
        {
            data["_csrf_token"] = "<new>";
        }
        return data;
    }

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    static string Value(string setCookie)
    {
        var start = setCookie.IndexOf('=', StringComparison.Ordinal) + 1;
        var end = setCookie.IndexOf(';', StringComparison.Ordinal);
        return setCookie[start..(end < 0 ? setCookie.Length : end)];
    }

    static string Attributes(string setCookie) => setCookie.Contains(';', StringComparison.Ordinal) ? setCookie[setCookie.IndexOf(';', StringComparison.Ordinal)..] : "";

    static IEnumerable<string> CompareEvents(JsonArray expected, IReadOnlyList<SeamEvent> actual)
    {
        var want = expected.Select(node => $"broadcast {node!["broadcast"]!.GetValue<string>()} {node["payload"]!.GetValue<string>()}").ToList();
        var have = actual.Select(seamEvent => seamEvent switch
        {
            Broadcast broadcast => $"broadcast {broadcast.Stream} {broadcast.Payload}",
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
        var want = Placeholders((JsonObject)expected.DeepClone()).ToJsonString(Unescaped);
        var have = actual.ToJsonString(Unescaped);
        if (want != have)
        {
            yield return $"changes {have}\n  expected {want}";
        }
    }

    // Every row of the tables, keyed by its first column, as the generator's `snapshot`, with the
    // random columns as placeholders.
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
        return Placeholders(changes);
    }

    static JsonObject Placeholders(JsonObject changes)
    {
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

    [GeneratedRegex("name=\"authenticity_token\" value=\"([^\"]*)\"")]
    private static partial Regex FormToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();
}
