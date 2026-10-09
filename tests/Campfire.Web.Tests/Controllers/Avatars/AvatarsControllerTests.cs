using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Storage.Media;
using Campfire.Vectors;
using Campfire.Web.Controllers;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.Avatars;

/// <summary>
/// Replays <c>Vectors/avatars.json</c>, what the reference did for each avatar request (see
/// <c>Vectors/generate.rb</c>), through the router and <see cref="UsersAvatarsController"/> on the
/// same database and storage, in the same order, with CSRF protection on. Each request must answer
/// as the reference did (status, headers, cookies, the SVG's text or the image's bytes) and change
/// the same rows to the same values. Random values (session tokens, password digests, blob keys)
/// read as placeholders.
/// </summary>
/// <remarks>
/// The seed users' uploads are served from the variants the seed already holds, so their bytes are
/// always compared. An avatar uploaded here is resized by libvips: those requests run only where
/// libvips is installed, and their bytes are compared only with the libvips that made the vectors.
/// </remarks>
public sealed partial class AvatarsControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Avatars/Vectors/avatars.json")))!;

    static readonly string[] Tables = ["users", "sessions", "active_storage_blobs", "active_storage_attachments", "active_storage_variant_records"];

    static readonly string[] ComparedHeaders =
        ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag", "last-modified", "content-disposition", "content-transfer-encoding"];

    static readonly HashSet<string> RandomColumns = ["token", "password_digest", "key"];

    // The requests that resize an avatar uploaded here: the first makes the variant, the second sends it.
    static readonly HashSet<string> NewVariantCases = ["a new avatar", "a new avatar, again"];

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    public AvatarsControllerTests() => StageSeedStorage(app);

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    static bool SameLibVips => LibVips.IsAvailable && LibVips.Version == Vectors["libvips"]!.GetValue<string>();

    /// <summary>The generator's STORAGE: each listed file under its seed blob's key.</summary>
    internal static void StageSeedStorage(MessagesApp app)
    {
        foreach (var entry in Vectors["storage"]!.AsArray())
        {
            var key = (string)app.Scalar($"SELECT key FROM active_storage_blobs WHERE id = {entry![0]!.GetValue<long>()}")!;
            var path = app.App.RequireStorage().Service.PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(VectorFiles.Root, entry[1]!.GetValue<string>()), path);
        }
    }

    [Fact]
    public async Task Each_request_answers_and_changes_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            var newVariant = NewVariantCases.Contains(name);
            if (newVariant && !LibVips.IsAvailable)
            {
                // Without libvips, the reference's rows stand in for the variant, so the ids of
                // later rows still line up.
                ApplyChanges(sample["changes"]!.AsObject());
                continue;
            }
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            var compareBytes = !newVariant || SameLibVips;
            failures.AddRange(CompareResponse(sample["response"]!, actual, compareBytes).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(sample["changes"]!.AsObject(), Changes(before, after), compareBytes).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        // Uploaded avatars are the seed's 512px WebP variants, bots without one get the stock icon,
        // everyone else their initials.
        Assert.Equal("image/webp", Header(Case("Jason's avatar"), "content-type"));
        Assert.Equal("image/webp", Header(Case("Deploy Bot's avatar"), "content-type"));
        Assert.Equal(Case("Jason's avatar")["response"]!["body_sha256"]!.GetValue<string>(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference-rust/vectors/storage/moon-avatar.webp")))));
        Assert.Contains("default-bot-avatar.svg", Header(Case("Bender's avatar"), "content-disposition"), StringComparison.Ordinal);
        Assert.Contains("\n      D\n", Case("David's initials")["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("textLength=\"85%\"", Case("three initials")["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("\n      PE\n", Case("initials when the avatar can't be resized")["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);

        // Cached publicly, revalidated by the user's ETag (with the template's digest when SVG is
        // accepted).
        Assert.Equal("max-age=1800, public, stale-while-revalidate=604800", Header(Case("David's initials"), "cache-control"));
        Assert.Equal(304, Status(Case("initials, cached")));
        Assert.Equal(304, Status(Case("uploaded avatar, cached")));
        Assert.NotEqual(Header(Case("David's initials"), "etag"), Header(Case("initials, SVG accepted"), "etag"));

        // Bad tokens are 404s; removing takes the signed-in person's own avatar and touches them.
        Assert.Equal(404, Status(Case("a token that isn't one")));
        Assert.Equal(404, Status(Case("a token for nobody")));
        Assert.Equal("http://campfire.test/users/me/profile", Header(Case("remove own avatar"), "location"));
        Assert.NotNull(Case("remove own avatar")["changes"]!["users"]);
        Assert.Null(Case("remove another's avatar")["changes"]!["active_storage_attachments"]);
        Assert.Contains("\n      J\n", Case("Jason's initials")["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_template_digest_is_the_reference_template_s()
    {
        var source = File.ReadAllBytes(Path.Combine(VectorFiles.Root, "reference/app/views/users/avatars/show.svg.erb"));
        var digest = Convert.ToHexStringLower(SHA256.HashData([.. source, (byte)'-']))[..32];

        Assert.Equal(UsersAvatarsController.ShowTemplateDigest, digest);
    }

    [Fact]
    public async Task New_avatars_are_byte_identical_to_the_reference()
    {
        Assert.SkipUnless(LibVips.IsAvailable, "libvips isn't installed");
        Assert.SkipUnless(SameLibVips, $"the vectors were made with libvips {Vectors["libvips"]}, this is libvips {LibVips.Version}");
        foreach (var sample in Cases().TakeWhile(sample => sample["name"]!.GetValue<string>() != "join with an avatar that can't be resized"))
        {
            var actual = await SendAsync(sample["request"]!);
            if (NewVariantCases.Contains(sample["name"]!.GetValue<string>()))
            {
                var expected = sample["response"]!;
                Assert.Equal(expected["body_sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(actual.Bytes)));
                Assert.Equal(expected["body_size"]!.GetValue<int>(), actual.Bytes.Length);
            }
        }
    }

    static JsonNode Case(string name) => Cases().Single(sample => sample["name"]?.GetValue<string>() == name);

    static int Status(JsonNode sample) => sample["response"]!["status"]!.GetValue<int>();

    static string Header(JsonNode sample, string name) => sample["response"]!["headers"]![name]!.GetValue<string>();

    static List<string> Cookies(JsonNode response) => response["headers"]!["set-cookie"] switch
    {
        JsonArray array => [.. array.Select(cookie => cookie!.GetValue<string>())],
        JsonValue single => [.. single.GetValue<string>().Split('\n')],
        _ => [],
    };

    // A request through the whole app, with a multipart body built from its parts as the generator
    // built it.
    async Task<Result> SendAsync(JsonNode request)
    {
        var target = request["path"]!.GetValue<string>();
        var context = new DefaultHttpContext();
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Method = request["method"]!.GetValue<string>();
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.7");
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

    static IEnumerable<string> CompareResponse(JsonNode expected, Result actual, bool compareBytes)
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
            // A variant made here is sent under its blob's random key.
            if (name == "content-disposition")
            {
                (want, have) = (BlobKeyFilename(want), BlobKeyFilename(have));
            }
            // An HTML page's ETag digests its body, CSRF token masks and all.
            if (name == "etag" && want is not null && (headers["content-type"]?.GetValue<string>() ?? "").StartsWith("text/html", StringComparison.Ordinal))
            {
                (want, have) = (want.Length > 0 ? "<etag>" : want, have is { Length: > 0 } ? "<etag>" : have);
            }
            if (!SameHeader(name, want, have))
            {
                yield return $"{name} {have ?? "(none)"}, expected {want ?? "(none)"}";
            }
        }
        var wantCookies = Cookies(expected).Select(CookieShape).Order(StringComparer.Ordinal).ToList();
        var haveCookies = actual.Headers.SetCookie.Select(cookie => CookieShape(cookie!)).Order(StringComparer.Ordinal).ToList();
        if (!wantCookies.SequenceEqual(haveCookies))
        {
            yield return $"cookies [{string.Join(", ", haveCookies)}], expected [{string.Join(", ", wantCookies)}]";
        }
        if (expected["body_sha256"] is { } sha256)
        {
            var (have, size) = (Convert.ToHexStringLower(SHA256.HashData(actual.Bytes)), actual.Bytes.Length);
            if (compareBytes && (have != sha256.GetValue<string>() || size != expected["body_size"]!.GetValue<int>()))
            {
                yield return $"body is {size} bytes with SHA-256 {have}, expected {expected["body_size"]} bytes with {sha256}";
            }
            yield break;
        }
        var (wantBody, haveBody) = (Normalize(expected["body"]!.GetValue<string>()), Normalize(Encoding.UTF8.GetString(actual.Bytes)));
        if (wantBody != haveBody)
        {
            yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
        }
    }

    // A cookie's name and attributes: session values are encrypted or signed afresh.
    static string CookieShape(string setCookie)
    {
        var end = setCookie.IndexOf(';', StringComparison.Ordinal);
        return setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)] + (end < 0 ? "" : setCookie[end..]);
    }

    static string? BlobKeyFilename(string? disposition) => disposition is null ? null : BlobKeyName().Replace(disposition, "<key>");

    // CSRF tokens are masked with random bytes.
    static string Normalize(string text) => CsrfMeta().Replace(AuthenticityToken().Replace(text, "$1<token>\""), "$1<token>\"");

    // Rails' error pages (PublicExceptions) spell the charset "UTF-8"; W01's ErrorPages writes
    // "utf-8" (reported on #22).
    static bool SameHeader(string name, string? want, string? have) =>
        name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal)
            ? string.Equals(want, have, StringComparison.OrdinalIgnoreCase)
            : want == have;

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

    // Random columns read as placeholders, and a variant's checksum and size too unless libvips
    // matches the one that made the vectors.
    static JsonObject Placeholders(JsonObject changes, bool compareBytes)
    {
        foreach (var (table, rows) in changes)
        {
            foreach (var change in rows!.AsArray())
            {
                if (change!["row"] is JsonObject row)
                {
                    IEnumerable<string> random = table == "active_storage_blobs" && !compareBytes ? [.. RandomColumns, "checksum", "byte_size"] : RandomColumns;
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
