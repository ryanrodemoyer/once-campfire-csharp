using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Controllers.ActiveStorage;

/// <summary>
/// Replays <c>Vectors/endpoints.json</c>, what the reference did for Active Storage's HTTP endpoints
/// (see <c>Vectors/generate.rb</c>), through the router on the same database and files, with CSRF
/// protection on. A signed URL the reference minted must answer as the reference did, and one this
/// port mints for the same blob must be the same string.
/// </summary>
public sealed partial class ActiveStorageControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/ActiveStorage/Vectors/endpoints.json")))!;

    static readonly string[] Tables = ["active_storage_blobs", "active_storage_attachments", "active_storage_variant_records"];

    static readonly string[] ComparedHeaders =
    [
        "content-type", "content-length", "content-range", "content-disposition", "content-transfer-encoding",
        "cache-control", "etag", "last-modified", "accept-ranges", "location", "vary", "date",
        "x-frame-options", "x-xss-protection", "x-content-type-options", "x-permitted-cross-domain-policies", "referrer-policy",
    ];

    static readonly DateTime Mtime = new(2026, 3, 2, 16, 0, 0, DateTimeKind.Utc);

    readonly MessagesApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    public ActiveStorageControllerTests()
    {
        StageStorage();
        using var connection = app.Open();
        foreach (var sql in Vectors["extra_sql"]!.AsArray())
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql!.GetValue<string>();
            command.ExecuteNonQuery();
        }
    }

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    static JsonNode Signed(string name) => Vectors["signed"]![name]!;

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

    /// <summary>
    /// The reference's signed paths, recomputed here for the same blob, expiry and disposition.
    /// The replay above is the other direction: those paths, requested on this port.
    /// </summary>
    [Fact]
    public async Task Signed_urls_match_the_reference_s()
    {
        var storage = app.App.RequireStorage();
        var now = app.Now;
        var token = TestContext.Current.CancellationToken;
        var moon = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, 5), token))!;
        var video = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, 9), token))!;
        var notes = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, 13), token))!;
        var bmp = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, 14), token))!;
        var lodz = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, Signed("lodz_id").GetValue<long>()), token))!;
        var thumbImage = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, Signed("thumb_image_id").GetValue<long>()), token))!;
        var posterImage = (await app.Database.ReadAsync(session => BlobRecords.FindBlob(session, Signed("poster_image_id").GetValue<long>()), token))!;
        // representation(variation) defaults a variable image to its own format before signing the key.
        // The BMP path reuses that same thumb segment. A preview keeps the variation it was given.
        var thumbKey = Representable.Variant(moon, new Transformations(("resize_to_limit", new object?[] { 1200L, 800L }))).Variation.Key(storage.Verifier);
        var posterKey = new Variation(new Transformations(("format", "webp"), ("resize_to_limit", new object?[] { 1200L, 800L }))).Key(storage.Verifier);

        Assert.Equal(Signed("moon_redirect").GetValue<string>(), storage.Urls.BlobRedirectPath(moon));
        Assert.Equal(Signed("moon_redirect_attachment").GetValue<string>(), storage.Urls.BlobRedirectPath(moon, "attachment"));
        Assert.Equal(Signed("moon_proxy").GetValue<string>(), storage.Urls.BlobProxyPath(moon));
        Assert.Equal(Signed("lodz_proxy").GetValue<string>(), storage.Urls.BlobProxyPath(lodz));
        Assert.Equal(Signed("notes_proxy").GetValue<string>(), storage.Urls.BlobProxyPath(notes));
        Assert.Equal(Signed("moon_disk").GetValue<string>(), DiskPath(storage, moon, now.AddMinutes(5), "inline"));
        Assert.Equal(Signed("moon_disk_attachment").GetValue<string>(), DiskPath(storage, moon, now.AddMinutes(5), "attachment"));
        Assert.Equal(Signed("moon_disk_no_expiry").GetValue<string>(), DiskPath(storage, moon, null, "inline"));
        Assert.Equal(Signed("video_disk_inline").GetValue<string>(), DiskPath(storage, video, now.AddMinutes(5), "inline"));
        Assert.Equal(Signed("thumb_disk").GetValue<string>(), DiskPath(storage, thumbImage, now.AddMinutes(5), "inline"));
        Assert.Equal(Signed("poster_disk").GetValue<string>(), DiskPath(storage, posterImage, now.AddMinutes(5), "inline"));
        Assert.Equal(Signed("thumb_redirect").GetValue<string>(), storage.Urls.RepresentationRedirectPath(moon, thumbKey));
        Assert.Equal(Signed("thumb_proxy").GetValue<string>(), storage.Urls.RepresentationProxyPath(moon, thumbKey));
        Assert.Equal(Signed("poster_redirect").GetValue<string>(), storage.Urls.RepresentationRedirectPath(video, posterKey));
        Assert.Equal(Signed("poster_proxy").GetValue<string>(), storage.Urls.RepresentationProxyPath(video, posterKey));
        Assert.Equal(Signed("bmp_representation").GetValue<string>(), storage.Urls.RepresentationRedirectPath(bmp, thumbKey));
        Assert.Equal(Signed("direct_upload").GetValue<string>(), storage.Service.DirectUploadPath(
            Signed("direct_upload_key").GetValue<string>(), now.AddMinutes(5),
            Signed("direct_upload_content_type").GetValue<string>(), Signed("direct_upload_byte_size").GetValue<long>(),
            Signed("direct_upload_checksum").GetValue<string>()));
    }

    [Fact]
    public void The_vectors_cover_ranges_and_rejected_uploads()
    {
        Assert.Equal(206, Status("disk range"));
        Assert.Equal(206, Status("disk range multi"));
        Assert.Equal(416, Status("disk range unsatisfiable"));
        Assert.Equal("Byte range unsatisfiable\n", Body("disk range unsatisfiable"));
        Assert.Equal(200, Status("disk head"));
        Assert.Equal(0, Case("disk head")["response"]!["body_size"]!.GetValue<int>());
        Assert.Equal(304, Status("disk not modified"));
        Assert.Equal(206, Status("blob proxy range"));
        Assert.Equal(416, Status("blob proxy range unsatisfiable"));
        Assert.Equal(304, Status("blob proxy not modified"));
        Assert.Equal(401, Status("direct upload anonymous"));
        Assert.Empty(Case("direct upload anonymous")["changes"]!.AsObject());
        Assert.Equal(422, Status("direct upload no csrf"));
        Assert.Equal(401, Status("disk put anonymous"));
        Assert.Equal(204, Status("disk put"));
        Assert.Equal("hello!", Body("disk show uploaded"));
    }

    static string DiskPath(BlobStorage storage, Blob blob, DateTimeOffset? expiresAt, string disposition) =>
        storage.Service.UrlPath(blob.Key, expiresAt, blob.Filename, blob.ContentTypeForServing, blob.ForcedDispositionForServing ?? disposition);

    void StageStorage()
    {
        var root = app.App.RequireStorage();
        foreach (var entry in Vectors["storage"]!.AsArray())
        {
            var key = entry!["key"]!.GetValue<string>();
            var path = root.Service.PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (entry["text"]?.GetValue<string>() is { } text)
            {
                File.WriteAllText(path, text);
            }
            else
            {
                File.Copy(Path.Combine(VectorFiles.Root, entry["file"]!.GetValue<string>()), path, overwrite: true);
            }
            File.SetLastWriteTimeUtc(path, Mtime);
        }
        foreach (var entry in Vectors["extra_files"]!.AsArray())
        {
            var path = root.Service.PathFor(entry!["key"]!.GetValue<string>());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromBase64String(entry["base64"]!.GetValue<string>()));
            File.SetLastWriteTimeUtc(path, Mtime);
        }
    }

    static JsonNode Case(string name) => Cases().Single(sample => sample["name"]?.GetValue<string>() == name);

    static int Status(string name) => Case(name)["response"]!["status"]!.GetValue<int>();

    static string Body(string name) => Case(name)["response"]!["body"]!.GetValue<string>();

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
        var bytes = request["body_base64"]?.GetValue<string>() is { } encoded ? Convert.FromBase64String(encoded) : [];
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await app.App.HandleAsync(context);
        return new Result(context.Response.StatusCode, context.Response.Headers, responseBody.ToArray());
    }

    static IEnumerable<string> CompareResponse(JsonNode expected, Result actual)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }
        var headers = expected["headers"]!.AsObject();
        string? wantBoundary = null;
        string? haveBoundary = null;
        foreach (var name in ComparedHeaders)
        {
            var want = headers[name]?.GetValue<string>();
            var have = actual.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
            if (name == "vary" && have is not null)
            {
                have = string.Join(',', have.Split(',').Select(part => part.Trim()).Where(part => part != "Accept-Encoding"));
                have = have.Length == 0 ? null : have;
            }
            // The generator calls Rails.application, which does not add Content-Length. The port's
            // outer deflater does, for a buffered body. A length the reference set is still compared.
            if (name == "content-length" && want is null)
            {
                continue;
            }
            if (name == "content-type")
            {
                (want, wantBoundary) = NormalizeBoundary(want);
                (have, haveBoundary) = NormalizeBoundary(have);
            }
            if (name == "etag" && expected["body_normalized"] is not null)
            {
                (want, have) = (want is { Length: > 0 } ? "<etag>" : want, have is { Length: > 0 } ? "<etag>" : have);
            }
            if (!SameHeader(name, want, have))
            {
                yield return $"{name} {have ?? "(none)"}, expected {want ?? "(none)"}";
            }
        }
        var wantBody = expected["body_normalized"]?.GetValue<string>();
        if (wantBody is not null)
        {
            var haveBody = NormalizeUpload(Encoding.UTF8.GetString(actual.Bytes));
            if (wantBody != haveBody)
            {
                yield return $"body differs: {FirstDifference(wantBody, haveBody)}";
            }
            yield break;
        }
        // send_blob_byte_range_data's multipart boundary is SecureRandom.hex: 32 characters every
        // time, so the body size matches the reference and the bytes do not.
        if (wantBoundary is not null || haveBoundary is not null)
        {
            if (wantBoundary is null || haveBoundary is null || wantBoundary.Length != haveBoundary.Length)
            {
                yield return $"multipart boundary {haveBoundary ?? "(none)"}, expected {wantBoundary ?? "(none)"}";
            }
            if (actual.Bytes.Length != expected["body_size"]!.GetValue<int>())
            {
                yield return $"body is {actual.Bytes.Length} bytes, expected {expected["body_size"]}";
            }
            yield break;
        }
        var haveSha = Convert.ToHexStringLower(SHA256.HashData(actual.Bytes));
        if (haveSha != expected["body_sha256"]!.GetValue<string>() || actual.Bytes.Length != expected["body_size"]!.GetValue<int>())
        {
            yield return $"body is {actual.Bytes.Length} bytes with SHA-256 {haveSha}, expected {expected["body_size"]} bytes with {expected["body_sha256"]}";
        }
    }

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual)
    {
        var want = Placeholders((JsonObject)expected.DeepClone()).ToJsonString();
        var have = Placeholders(actual).ToJsonString();
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

    static JsonObject Placeholders(JsonObject changes)
    {
        foreach (var (_, rows) in changes)
        {
            foreach (var change in rows!.AsArray())
            {
                if (change!["row"] is JsonObject row && row["key"] is not null)
                {
                    row["key"] = "<random>";
                }
            }
        }
        return changes;
    }

    static bool SameHeader(string name, string? want, string? have) =>
        name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal)
            ? string.Equals(want, have, StringComparison.OrdinalIgnoreCase)
            : want == have;

    static (string? Header, string? Boundary) NormalizeBoundary(string? header)
    {
        if (header is null)
        {
            return (null, null);
        }
        var at = header.IndexOf("boundary=", StringComparison.Ordinal);
        if (at < 0)
        {
            return (header, null);
        }
        return (header[..(at + "boundary=".Length)] + "<boundary>", header[(at + "boundary=".Length)..]);
    }

    static string NormalizeUpload(string text) =>
        UploadToken().Replace(UploadSignedId().Replace(UploadKey().Replace(text, "\"key\":\"<key>\""), "\"signed_id\":\"<signed_id>\""), "$1<token>");

    static string FirstDifference(string want, string have)
    {
        var at = 0;
        while (at < want.Length && at < have.Length && want[at] == have[at])
        {
            at++;
        }
        var from = Math.Max(0, at - 80);
        var wantEnd = Math.Min(want.Length, at + 120);
        var haveEnd = Math.Min(have.Length, at + 120);
        return $"at {at}:\n    got      {have[from..haveEnd]}\n    expected {want[from..wantEnd]}";
    }

    public void Dispose() => app.Dispose();

    sealed record Result(int Status, IHeaderDictionary Headers, byte[] Bytes);

    [GeneratedRegex("\"key\":\"[^\"]+\"")]
    private static partial Regex UploadKey();

    [GeneratedRegex("\"signed_id\":\"[^\"]+\"")]
    private static partial Regex UploadSignedId();

    [GeneratedRegex("(/rails/active_storage/disk/)[^\"]+")]
    private static partial Regex UploadToken();
}
