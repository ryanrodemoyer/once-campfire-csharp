using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Signing;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.Profiles;

/// <summary>
/// Replays <c>Vectors/profiles.json</c>, what the reference did for each sign-in (transfer) link and
/// each look at and change to a person's own profile (see <c>Vectors/generate.rb</c>), through the
/// router, Sessions::TransfersController and Users::ProfilesController on the same database, in
/// the same order, with CSRF protection on. Each request must answer as the reference did (status,
/// headers, cookies, body), send the same broadcasts and change the same rows to the same values.
/// Random values (session tokens, password digests, blob keys, CSRF masks, new session ids) read as
/// placeholders.
/// </summary>
public sealed partial class ProfilesReplayTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Profiles/Vectors/profiles.json")))!;

    static readonly string[] Tables =
        ["users", "sessions", "active_storage_blobs", "active_storage_attachments"];

    static readonly string[] ComparedHeaders =
        ["content-type", "location", "vary", "cache-control", "x-frame-options"];

    // Columns holding random values: compared as present or absent.
    static readonly HashSet<string> RandomColumns = ["token", "password_digest", "key"];

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
            app.Seams.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), app.Seams.Events).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), Changes(before, after)).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void Transfer_ids_minted_here_are_the_ones_the_reference_mints()
    {
        // The same id for the same user at the same time, so a link minted on either works on
        // the other: the reference's ids are what the replayed transfers follow.
        foreach (var (userId, transferId) in Vectors["transfer_ids"]!.AsObject())
        {
            var id = long.Parse(userId, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(transferId!.GetValue<string>(), TransferableUser.GenerateTransferId(app.Keys, id, app.Now));
            Assert.Equal(id, TransferableUser.VerifyTransferId(app.Keys, transferId.GetValue<string>(), app.Now));
        }
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        // A link the reference minted signs its active user in, until it expires.
        foreach (var signedIn in new[] { "transfer", "transfer again", "transfer, PATCH", "transfer, a bot", "transfer, about to expire" })
        {
            Assert.Equal(302, Case(signedIn)["response"]!["status"]!.GetValue<int>());
            Assert.Contains(Cookies(Case(signedIn)["response"]!), cookie => cookie.StartsWith("session_token=", StringComparison.Ordinal));
            Assert.NotNull(Case(signedIn)["changes"]!["sessions"]);
        }
        Assert.Equal("http://campfire.test/rooms/201306877", Case("transfer, returning")["response"]!["headers"]!["location"]!.GetValue<string>());
        foreach (var refused in new[] { "transfer, expired", "transfer, tampered", "transfer, avatar token", "transfer, deactivated person", "transfer, banned person", "transfer, missing person" })
        {
            Assert.Equal(400, Case(refused)["response"]!["status"]!.GetValue<int>());
        }

        // The profile page carries the person's own transfer link.
        var kevinsLink = $"http://campfire.test/session/transfers/{Vectors["transfer_ids"]!["712064548"]!.GetValue<string>()}";
        Assert.Contains(kevinsLink, Case("profile, member")["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);

        // Updates change only the signed-in person (the notice rides in the session cookie, which
        // the replay compares).
        Assert.Equal(712064548, Case("update through another id")["changes"]!["users"]![0]!["id"]!.GetValue<long>());
        Assert.NotNull(Case("upload an avatar")["changes"]!["active_storage_attachments"]);
        Assert.Null(Case("remove the avatar")["changes"]!["active_storage_attachments"]![0]!["row"]);
    }

    static JsonNode Case(string name) => Cases().Single(sample => sample["name"]?.GetValue<string>() == name);

    static List<string> Cookies(JsonNode response) => response["headers"]!["set-cookie"] switch
    {
        JsonArray array => [.. array.Select(cookie => cookie!.GetValue<string>())],
        JsonValue single => [.. single.GetValue<string>().Split('\n')],
        _ => [],
    };

    // A request through the whole app from the case's remote address, with a multipart body built
    // from its parts as the generator built it.
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
        var bytes = request["multipart"] is JsonObject multipart ? MultipartBody(multipart) : Encoding.UTF8.GetBytes(request["body"]?.GetValue<string>() ?? "");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await app.App.HandleAsync(context);
        return new Result(context.Response.StatusCode, context.Response.Headers, responseBody.ToArray());
    }

    // The generator's `multipart_body`.
    static byte[] MultipartBody(JsonObject form)
    {
        var boundary = form["boundary"]!.GetValue<string>();
        using var body = new MemoryStream();
        void Write(string text) => body.Write(Encoding.UTF8.GetBytes(text));
        foreach (var part in form["parts"]!.AsArray())
        {
            var name = part!["name"]!.GetValue<string>();
            if (part["file"]?.GetValue<string>() is { } file)
            {
                Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{part["filename"]!.GetValue<string>()}\"\r\n" +
                    $"Content-Type: {part["content_type"]!.GetValue<string>()}\r\n\r\n");
                body.Write(File.ReadAllBytes(Path.Combine(VectorFiles.Root, file)));
                Write("\r\n");
            }
            else
            {
                Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{part["value"]!.GetValue<string>()}\r\n");
            }
        }
        Write($"--{boundary}--\r\n");
        return body.ToArray();
    }

    IEnumerable<string> CompareResponse(JsonNode expected, Result actual)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }
        var headers = expected["headers"]!.AsObject();
        // An HTML page's ETag digests its body, CSRF token masks and all.
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
        foreach (var failure in CompareCookies(Cookies(expected), [.. actual.Headers.SetCookie.Select(cookie => cookie!)]))
        {
            yield return failure;
        }
        var (wantBody, haveBody) = (Normalize(expected["body"]!.GetValue<string>()), Normalize(Encoding.UTF8.GetString(actual.Bytes)));
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
                // Signs a new session's random token, or re-signs the fixture's: compare that one is set.
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
        if (data["_csrf_token"]?.GetValue<string>() is { } csrf && csrf != "a02ProfileCsrfToken00a02ProfileCsrfToken00A")
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

    // Broadcasts, in order. The generator also records the jobs Active Storage enqueues (AnalyzeJob
    // for an upload, PurgeJob for a replaced or removed avatar), which aren't seam events here.
    static IEnumerable<string> CompareEvents(JsonArray expected, IReadOnlyList<SeamEvent> actual)
    {
        var want = expected.Where(node => node!["broadcast"] is not null).Select(node => $"broadcast {node!["broadcast"]!.GetValue<string>()} {node["payload"]!.GetValue<string>()}").ToList();
        var have = actual.OfType<Broadcast>().Select(broadcast => $"broadcast {broadcast.Stream} {broadcast.Payload}").ToList();
        if (!want.SequenceEqual(have))
        {
            yield return $"events [{string.Join(", ", have)}], expected [{string.Join(", ", want)}]";
        }
    }

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual)
    {
        var want = Placeholders((JsonObject)expected.DeepClone()).ToJsonString(Unescaped);
        var have = Placeholders(actual).ToJsonString(Unescaped);
        if (want != have)
        {
            yield return $"changes {have}\n  expected {want}";
        }
    }

    // Every row of the tables, keyed by its first column, as the generator's `snapshot`.
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
        return changes;
    }

    static JsonObject Placeholders(JsonObject changes)
    {
        foreach (var (_, rows) in changes)
        {
            foreach (var change in rows!.AsArray())
            {
                if (change!["row"] is JsonObject row)
                {
                    foreach (var column in RandomColumns.Where(column => row[column] is not null))
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

    sealed record Result(int Status, IHeaderDictionary Headers, byte[] Bytes);

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();
}
