using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.RailsCompat.Cookies;
using Campfire.Storage.Media;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.Accounts;

/// <summary>
/// Replays <c>Vectors/accounts.json</c>, what the reference did for each join, person's page and
/// account administration request (see <c>Vectors/generate.rb</c>), through the router and the
/// users, accounts, join code, logo and custom styles controllers on the same database, in the
/// same order, with CSRF protection on. Each request must answer as the reference did (status,
/// headers, cookies, body), send the same broadcasts and change the same rows to the same values.
/// Random values (session tokens, password digests, join codes, blob keys, CSRF masks, new session
/// ids) read as placeholders.
/// </summary>
/// <remarks>
/// Logo variants are made by libvips: those requests run only where libvips is installed, and
/// their bytes (the response, and the variant blob's checksum and size) are compared only with the
/// libvips that made the vectors. The stock icons' bytes are always compared.
/// </remarks>
public sealed partial class AccountsControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Accounts/Vectors/accounts.json")))!;

    static readonly string[] Tables =
        ["accounts", "users", "memberships", "sessions", "active_storage_blobs", "active_storage_attachments", "active_storage_variant_records"];

    static readonly string[] ComparedHeaders =
        ["content-type", "location", "vary", "cache-control", "x-frame-options", "content-disposition", "content-transfer-encoding"];

    // Columns holding random values: compared as present or absent. Memberships are stamped by
    // SQLite's CURRENT_TIMESTAMP (insert_all), which the reference's frozen clock stops and ours
    // doesn't.
    static readonly HashSet<string> RandomColumns = ["token", "password_digest", "join_code", "key"];

    static readonly HashSet<string> SqliteStampedTables = ["memberships"];

    // A variant's bytes are libvips's: its blob's checksum and size.
    static readonly HashSet<string> VariantColumns = ["checksum", "byte_size"];

    // The requests that make logo variants.
    static readonly HashSet<string> LogoVariantCases = ["logo", "logo, small", "logo, again"];

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    static bool SameLibVips => LibVips.IsAvailable && LibVips.Version == Vectors["libvips"]!.GetValue<string>();

    [Fact]
    public async Task Each_request_answers_and_changes_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            var logoVariant = LogoVariantCases.Contains(name);
            if (logoVariant && !LibVips.IsAvailable)
            {
                // Without libvips, the reference's rows stand in for the variant, so the ids of
                // later rows still line up.
                ApplyChanges(sample["changes"]!.AsObject());
                continue;
            }
            app.Seams.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            var compareBytes = !logoVariant || SameLibVips;
            failures.AddRange(CompareResponse(sample["response"]!, actual, compareBytes).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), app.Seams.Events).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), Changes(before, after), compareBytes).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        // Joining signs the new person in and gives them the open rooms; a taken address goes to
        // the sign-in page.
        var join = Case("join");
        Assert.Equal(302, join["response"]!["status"]!.GetValue<int>());
        Assert.Contains(Cookies(join["response"]!), cookie => cookie.StartsWith("session_token=", StringComparison.Ordinal));
        Assert.NotEmpty(join["changes"]!["memberships"]!.AsArray());
        Assert.Equal("http://campfire.test/session/new?email_address=david%4037signals.com",
            Case("join, address taken")["response"]!["headers"]!["location"]!.GetValue<string>());
        Assert.Equal(404, Case("join page, wrong code")["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, Case("join page, old code")["response"]!["status"]!.GetValue<int>());

        // Only administrators change the account.
        foreach (var forbidden in new[] { "rename, member", "delete the logo, member", "new join code, member", "custom styles, member", "save custom styles, member" })
        {
            Assert.Equal(403, Case(forbidden)["response"]!["status"]!.GetValue<int>());
        }
        Assert.NotNull(Case("new join code")["changes"]!["accounts"]);

        // The logo: 512 and 192 pixel PNGs of an upload, else the stock icons.
        Assert.Equal("image/png", Case("logo")["response"]!["headers"]!["content-type"]!.GetValue<string>());
        Assert.NotNull(Case("logo")["changes"]!["active_storage_variant_records"]);
        Assert.Contains("app-icon.png", Case("logo that can't be resized")["response"]!["headers"]!["content-disposition"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(304, Case("stock logo, cached")["response"]!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Logo_variants_are_byte_identical_to_the_reference()
    {
        Assert.SkipUnless(LibVips.IsAvailable, "libvips isn't installed");
        Assert.SkipUnless(SameLibVips, $"the vectors were made with libvips {Vectors["libvips"]}, this is libvips {LibVips.Version}");
        foreach (var sample in Cases().TakeWhile(sample => sample["name"]!.GetValue<string>() != "settings with a logo"))
        {
            var actual = await SendAsync(sample["request"]!);
            if (LogoVariantCases.Contains(sample["name"]!.GetValue<string>()))
            {
                var expected = sample["response"]!;
                Assert.Equal(expected["body_sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(actual.Bytes)));
                Assert.Equal(expected["body_size"]!.GetValue<int>(), actual.Bytes.Length);
            }
        }
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

    IEnumerable<string> CompareResponse(JsonNode expected, Result actual, bool compareBytes)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }
        var headers = expected["headers"]!.AsObject();
        var binary = expected["body_sha256"] is not null;
        // An HTML page's ETag digests its body, CSRF token masks and all; a logo's is the account's.
        foreach (var name in binary || status == 304 ? [.. ComparedHeaders, "etag"] : ComparedHeaders)
        {
            var want = headers[name]?.GetValue<string>();
            var have = actual.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
            // The generator calls Rails.application, inside config.ru's Rack::Deflater.
            if (name == "vary" && have is not null)
            {
                have = string.Join(',', have.Split(',').Where(part => part != "Accept-Encoding"));
                have = have.Length == 0 ? null : have;
            }
            if (name == "content-disposition")
            {
                (want, have) = (BlobKeyFilename(want), BlobKeyFilename(have));
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
        if (binary)
        {
            var (sha256, size) = (Convert.ToHexStringLower(SHA256.HashData(actual.Bytes)), actual.Bytes.Length);
            if (compareBytes && (sha256 != expected["body_sha256"]!.GetValue<string>() || size != expected["body_size"]!.GetValue<int>()))
            {
                yield return $"body is {size} bytes with SHA-256 {sha256}, expected {expected["body_size"]} bytes with {expected["body_sha256"]}";
            }
            yield break;
        }
        var (wantBody, haveBody) = (Normalize(expected["body"]!.GetValue<string>()), Normalize(Encoding.UTF8.GetString(actual.Bytes)));
        if (wantBody != haveBody)
        {
            yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
        }
    }

    // A variant is sent under its blob's random key.
    static string? BlobKeyFilename(string? disposition) => disposition is null ? null : BlobKeyName().Replace(disposition, "<key>");

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
        if (data["_csrf_token"]?.GetValue<string>() is { } csrf && csrf != "a03AccountsCsrfToken0a03AccountsCsrfToken0A")
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
        var have = actual.OfType<Broadcast>().Select(broadcast => $"broadcast {broadcast.Stream} {broadcast.Payload}").ToList();
        if (!want.SequenceEqual(have))
        {
            yield return $"events [{string.Join(", ", have)}], expected [{string.Join(", ", want)}]";
        }
    }

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual, bool compareBytes)
    {
        var want = Placeholders((JsonObject)expected.DeepClone(), compareBytes).ToJsonString(Unescaped);
        var have = Placeholders(actual, compareBytes).ToJsonString(Unescaped);
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

    // Writes a case's recorded changes: each row as the reference left it, or gone.
    void ApplyChanges(JsonObject changes)
    {
        using var connection = app.Open();
        foreach (var (table, rows) in changes)
        {
            foreach (var change in rows!.AsArray())
            {
                using var command = connection.CreateCommand();
                if (change!["row"] is JsonObject row)
                {
                    var columns = row.Select(column => column.Key).ToList();
                    command.CommandText = $"INSERT OR REPLACE INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))})";
                    for (var i = 0; i < columns.Count; i++)
                    {
                        command.Parameters.AddWithValue($"@p{i}", row[columns[i]] is JsonValue value ? value.GetValue<JsonElement>().ValueKind switch
                        {
                            JsonValueKind.Number => value.GetValue<JsonElement>().GetInt64(),
                            _ => value.GetValue<JsonElement>().GetString(),
                        } : DBNull.Value);
                    }
                }
                else
                {
                    command.CommandText = $"DELETE FROM {table} WHERE id = @id";
                    command.Parameters.AddWithValue("@id", change["id"]!.GetValue<long>());
                }
                command.ExecuteNonQuery();
            }
        }
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

    static JsonObject Placeholders(JsonObject changes, bool compareBytes)
    {
        foreach (var (table, rows) in changes)
        {
            foreach (var change in rows!.AsArray())
            {
                if (change!["row"] is JsonObject row)
                {
                    IEnumerable<string> random = SqliteStampedTables.Contains(table) ? [.. RandomColumns, "created_at", "updated_at"] : RandomColumns;
                    if (table == "active_storage_blobs" && !compareBytes)
                    {
                        random = [.. random, .. VariantColumns];
                    }
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

    sealed record Result(int Status, IHeaderDictionary Headers, byte[] Bytes);

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("(?<=filename\\*?=(\"|UTF-8''))[a-z0-9]{28}(?=\"|$)")]
    private static partial Regex BlobKeyName();
}
