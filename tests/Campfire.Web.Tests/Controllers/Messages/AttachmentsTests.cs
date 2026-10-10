using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.Jobs.Runner;
using Campfire.Storage.Media;
using Campfire.Vectors;
using Campfire.Web.Controllers;
using Campfire.Web.Tests.Pipeline;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// Replays <c>Vectors/attachments.json</c>, what the reference did when the composer's file
/// uploader uploaded an image, a video and a plain file (see <c>Vectors/generate.rb</c>) and when
/// each message was destroyed, through the router and MessagesWriteController on the same database,
/// in the same order, with CSRF protection on. Each request must answer as the reference did
/// (status, headers, cookies, body), send the same broadcasts and jobs in the same order — including
/// the Active Storage jobs (AnalyzeJob for every attached blob, PurgeJob for a destroyed message's
/// blob) — and change the same rows to the same values.
/// <para>
/// The media a request generates (a thumbnail, a video preview and its webp variant) is the
/// toolchain's: those blobs' bytes are compared when this machine's libvips and ffmpeg are the ones
/// that made the vectors (the pinned toolchain, P02's image), and read as placeholders otherwise.
/// S03's goldens prove the bytes themselves.
/// </para>
/// </summary>
public sealed partial class AttachmentsTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(
        Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/attachments.json")))!;

    static readonly string[] Tables =
        ["messages", "action_text_rich_texts", "rooms", "memberships", "boosts", "sessions",
         "active_storage_attachments", "active_storage_blobs", "active_storage_variant_records", "message_search_index"];

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag"];

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly AttachmentsApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    static JsonNode Case(string name) => Cases().Single(sample => sample["name"]?.GetValue<string>() == name);

    [Fact]
    public async Task Each_upload_answers_and_changes_what_the_reference_did()
    {
        RequireMediaTools();
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            app.Recording.Clear();
            var before = Snapshot();
            var actual = await SendAsync(sample["request"]!);
            var after = Snapshot();

            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareEvents(sample["events"]!.AsArray(), app.Recording.Events).Select(failure => $"{name}: {failure}"));
            failures.AddRange(CompareChanges(
                sample["changes"]!.AsObject(),
                Changes(before, after),
                sample["media_blob_ids"]!.AsArray().Select(id => id!.GetValue<long>()).ToList()).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        // message/attachment_test.rb's cases, as the replay's rows show them: an image gets a
        // thumbnail, a video a preview, and a message with no body indexes its filename.
        var image = Case("member uploads an image");
        Assert.Equal(200, image["response"]!["status"]!.GetValue<int>());
        Assert.Single(image["changes"]!["active_storage_variant_records"]!.AsArray());
        Assert.Contains(image["changes"]!["active_storage_attachments"]!.AsArray(),
            row => row!["row"]!["record_type"]!.GetValue<string>() == "ActiveStorage::VariantRecord");
        Assert.Equal("moon.jpg", Indexed(image, "m10-hq-moon"));

        var video = Case("member uploads a video");
        Assert.Equal(200, video["response"]!["status"]!.GetValue<int>());
        Assert.Contains(video["changes"]!["active_storage_attachments"]!.AsArray(),
            row => row!["row"]!["name"]!.GetValue<string>() == "preview_image"
                && row!["row"]!["record_type"]!.GetValue<string>() == "ActiveStorage::Blob");
        Assert.Equal("alpha-centuri.mov", Indexed(video, "m10-hq-mov"));

        var plain = Case("member uploads a plain file");
        Assert.Equal(200, plain["response"]!["status"]!.GetValue<int>());
        Assert.Null(plain["changes"]!["active_storage_variant_records"]);
        Assert.Equal("pixel.bmp", Indexed(plain, "m10-hq-bmp"));

        // The uploader's XHR answers with the turbo stream, whatever Accept it sends.
        Assert.Equal("text/vnd.turbo-stream.html; charset=utf-8", Case("member uploads an image")["response"]!["headers"]!["content-type"]!.GetValue<string>());
        // The gem's jobs: AnalyzeJob for every attached blob, PurgeJob for the destroyed message's.
        Assert.Equal("ActiveStorage::AnalyzeJob", Case("member uploads an image")["events"]![2]!["job"]!.GetValue<string>());
        Assert.Equal("ActiveStorage::PurgeJob", Case("member destroys the image message")["events"]![0]!["job"]!.GetValue<string>());
        // A bad signed id is a 500, as it is for an avatar (A02).
        Assert.Equal(500, Case("upload with a bad signed id")["response"]!["status"]!.GetValue<int>());
    }

    /// <summary>
    /// The gem's jobs, performed: every AnalyzeJob leaves its blob analyzed (the controller's own
    /// synchronous analyze left the uploaded one), and PurgeJob takes the blob's row and files with
    /// its variant records' images and its preview image, whose blobs are purged later in turn.
    /// </summary>
    [Fact]
    public async Task The_attachment_jobs_do_what_the_gem_jobs_do()
    {
        RequireMediaTools();
        var recording = new RecordingSeams();
        var logs = new List<string>();
        await using var runner = new JobRunner((level, message, _) => logs.Add($"{level}: {message}"));
        using var app = new AttachmentsApp(
            DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
            Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()),
            new DomainSeams(recording, runner, recording));
        new MessageAttachmentJobs(app.Database, app.App.Storage!, app.Keys, new FixedClock(app.Now)).RegisterWith(runner);

        // An image upload: the message's blob and the thumbnail's, both analyzed by their jobs.
        var moonUpload = UploadBody(new Upload("moon.jpg", "image/jpeg", "m10-jobs-moon"));
        var uploaded = await app.SendAsync("POST", "/rooms/201306877/messages", app.Uploader("KevinSessionToken0000001"),
            moonUpload.Body, moonUpload.ContentType);
        Assert.True(uploaded.Status == 200, $"body: {uploaded.Body}");
        var moon = BlobId(app, "m10-jobs-moon");
        var thumb = (long)app.Scalar(
            $"SELECT b.id FROM active_storage_variant_records v JOIN active_storage_attachments a ON a.record_id = v.id JOIN active_storage_blobs b ON b.id = a.blob_id WHERE v.blob_id = {moon} AND a.record_type = 'ActiveStorage::VariantRecord' AND a.name = 'image'")!;
        await UntilAsync(() => Analyzed(app, moon) && Analyzed(app, thumb), logs);
        Assert.Equal("""{"identified":true,"width":640,"height":640,"analyzed":true}""", Metadata(app, moon));
        Assert.Equal("""{"identified":true,"width":640,"height":640,"analyzed":true}""", Metadata(app, thumb));

        // Destroying the message purges the blob: its row, its variant record and its image, and
        // both files, go.
        var moonPath = PathFor(app, moon);
        var thumbPath = PathFor(app, thumb);
        Assert.True(File.Exists(moonPath));
        Assert.True(File.Exists(thumbPath));
        var destroyed = await app.SendAsync("DELETE", "/rooms/201306877/messages/" + MessageId(app, "m10-jobs-moon"),
            app.Uploader("KevinSessionToken0000001"));
        Assert.Equal(200, destroyed.Status);
        await UntilAsync(() => BlobGone(app, moon) && BlobGone(app, thumb), logs);
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM active_storage_variant_records WHERE blob_id = {moon}"));
        Assert.Equal(0L, app.Scalar($"SELECT COUNT(*) FROM active_storage_attachments WHERE blob_id IN ({moon}, {thumb})"));
        Assert.False(File.Exists(moonPath));
        Assert.False(File.Exists(thumbPath));

        // A video upload, destroyed: the preview image and its webp variant go with the video's
        // blob, each purge cascading into the next.
        var movUpload = UploadBody(new Upload("alpha-centuri.mov", "video/quicktime", "m10-jobs-mov"));
        var videoUploaded = await app.SendAsync("POST", "/rooms/201306877/messages", app.Uploader("KevinSessionToken0000001"),
            movUpload.Body, movUpload.ContentType);
        Assert.Equal(200, videoUploaded.Status);
        var mov = BlobId(app, "m10-jobs-mov");
        var previewImage = (long)app.Scalar(
            $"SELECT b.id FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id WHERE a.record_type = 'ActiveStorage::Blob' AND a.name = 'preview_image' AND a.record_id = {mov}")!;
        var webp = (long)app.Scalar(
            $"SELECT b.id FROM active_storage_variant_records v JOIN active_storage_attachments a ON a.record_id = v.id JOIN active_storage_blobs b ON b.id = a.blob_id WHERE v.blob_id = {previewImage} AND a.record_type = 'ActiveStorage::VariantRecord' AND a.name = 'image'")!;
        await app.SendAsync("DELETE", "/rooms/201306877/messages/" + MessageId(app, "m10-jobs-mov"),
            app.Uploader("KevinSessionToken0000001"));
        await UntilAsync(() => BlobGone(app, mov) && BlobGone(app, previewImage) && BlobGone(app, webp), logs);
    }

    // `require "vips"` and ffmpeg, as the reference needs them: the tests skip when they're not
    // installed, as S03's media goldens do.
    static void RequireMediaTools()
    {
        if (!LibVips.IsAvailable)
        {
            Assert.Skip("libvips isn't installed");
        }
        if (!VideoPreviewer.IsAvailable)
        {
            Assert.Skip("ffmpeg isn't installed");
        }
        if (FirstLine("ffprobe") is null)
        {
            Assert.Skip("ffprobe isn't installed");
        }
    }

    // `message_search_index`'s row for a message the case just made, by its client message id.
    static string Indexed(JsonNode @case, string clientMessageId)
    {
        var id = @case["changes"]!["messages"]!.AsArray()
            .Single(row => row!["row"]!["client_message_id"]!.GetValue<string>() == clientMessageId)!["id"]!.GetValue<long>();
        return @case["changes"]!["message_search_index"]!.AsArray()
            .Single(row => row!["id"]!.GetValue<long>() == id)!["row"]!["body"]!.GetValue<string>();
    }

    // A request through the whole app from the vectors' multipart body, as the generator built it
    // (a destroy sends none: its body is empty).
    Task<Response> SendAsync(JsonNode request)
    {
        var multipart = request["multipart"]?.AsObject();
        var headers = request["headers"]!.AsObject().ToDictionary(header => header.Key, header => header.Value!.GetValue<string>());
        var body = multipart is null ? null : MultipartBody(multipart);
        var contentType = multipart is null ? null : $"multipart/form-data; boundary={multipart["boundary"]!.GetValue<string>()}";
        return app.SendAsync(request["method"]!.GetValue<string>(), request["path"]!.GetValue<string>(), headers, body, contentType);
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

    // The uploader's FormData: the file, then the client message id.
    sealed record Upload(string Filename, string ContentType, string ClientMessageId);

    // The request's body and Content-Type for the uploader's POST.
    sealed record UploadForm(byte[] Body, string ContentType);

    static UploadForm UploadBody(Upload upload)
    {
        const string boundary = "----m10UploadBoundary7MA4YWxkTrZu0gW";
        return new(
            MultipartBody(new JsonObject
            {
                ["boundary"] = boundary,
                ["parts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "message[attachment]",
                        ["filename"] = upload.Filename,
                        ["content_type"] = upload.ContentType,
                        ["file"] = $"reference/test/fixtures/files/{upload.Filename}",
                    },
                    new JsonObject { ["name"] = "message[client_message_id]", ["value"] = upload.ClientMessageId },
                },
            }),
            $"multipart/form-data; boundary={boundary}");
    }

    static IEnumerable<string> CompareResponse(JsonNode expected, Response actual)
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
            // A body that still carries random values (a message created without a client message
            // id) has a different Rack::ETag digest each time.
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

    // A message created without a client message id gets a random UUID.
    static string Normalize(string text) => Uuid().Replace(text, "<uuid>");

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

    static IEnumerable<string> CompareCookies(List<string> want, List<string> have)
    {
        var wantNames = want.Select(CookieName).ToList();
        var haveNames = have.Select(CookieName).ToList();
        if (!wantNames.SequenceEqual(haveNames))
        {
            yield return $"cookies [{string.Join(", ", haveNames)}], expected [{string.Join(", ", wantNames)}]";
            yield break;
        }
        foreach (var (expected, actual) in want.Zip(have))
        {
            var name = CookieName(expected);
            if (name == "_campfire_session")
            {
                // Encrypted with a random IV: compare that it's set (its contents — the session id
                // and the CSRF token — are the vectors' own).
                if (!actual.StartsWith("_campfire_session=", StringComparison.Ordinal))
                {
                    yield return $"session cookie {actual}, expected a session cookie";
                }
            }
            else if (expected != actual)
            {
                yield return $"cookie {actual}, expected {expected}";
            }
        }
    }

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    // Broadcasts and jobs, in order: `broadcast <stream> <payload>` and `job <class> <record ids>`,
    // the job arguments as the GlobalIDs the reference serializes records to, the payloads
    // normalized like bodies.
    static IEnumerable<string> CompareEvents(JsonArray expected, IReadOnlyList<SeamEvent> actual)
    {
        var want = expected.Select(node => node!["broadcast"] is { } stream
            ? $"broadcast {stream.GetValue<string>()} {Normalize(node["payload"]!.GetValue<string>())}"
            : $"job {node["job"]!.GetValue<string>()} {string.Join(',', node["arguments"]!.AsArray().Select(argument => GidId().Match(argument!.GetValue<string>()).Groups[1].Value))}").ToList();
        var have = actual.Select(seamEvent => seamEvent switch
        {
            Broadcast broadcast => $"broadcast {broadcast.Stream} {Normalize(broadcast.Payload)}",
            Enqueued enqueued => $"job {enqueued.Job.ClassName} {string.Join(',', enqueued.Job.ArgumentIds)}",
            _ => seamEvent.ToString(),
        }).ToList();
        if (!want.SequenceEqual(have))
        {
            yield return $"events\n    [{string.Join("\n     ", have)}]\n  expected\n    [{string.Join("\n     ", want)}]";
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

    static IEnumerable<string> CompareChanges(JsonObject expected, JsonObject actual, IReadOnlyList<long> mediaBlobIds)
    {
        var want = NormalizeChanges((JsonObject)expected.DeepClone(), mediaBlobIds);
        var have = NormalizeChanges((JsonObject)actual.DeepClone(), mediaBlobIds);
        if (want != have)
        {
            yield return $"changes {have}\n  expected {want}";
        }
    }

    // The changes with every blob key (randomly generated) as a placeholder, the media the request
    // generated (a thumbnail, a preview, a webp variant) as placeholders unless this machine's
    // tools are the ones that made the vectors, and a message created without a client message id
    // as one with a random UUID.
    static string NormalizeChanges(JsonObject changes, IReadOnlyList<long> mediaBlobIds)
    {
        if (changes["active_storage_blobs"] is JsonArray blobs)
        {
            foreach (var entry in blobs)
            {
                if (entry!["row"]?.AsObject() is not { } row)
                {
                    continue;
                }
                row["key"] = "<key>";
                if (mediaBlobIds.Contains(entry["id"]!.GetValue<long>()) && !SameToolchain)
                {
                    row["checksum"] = "<checksum>";
                    row["byte_size"] = "<size>";
                }
            }
        }
        return Uuid().Replace(changes.ToJsonString(Unescaped), "<uuid>");
    }

    // The toolchain that made the vectors' media, as storage.json records it.
    static bool SameToolchain =>
        LibVips.Version == Vectors["versions"]!["libvips"]!.GetValue<string>() &&
        FfmpegVersion() == Vectors["versions"]!["ffmpeg"]!.GetValue<string>();

    static readonly Lazy<string?> Ffmpeg = new(() => FirstLine("ffmpeg"));

    static string? FfmpegVersion() => Ffmpeg.Value;

    static string? FirstLine(string program)
    {
        try
        {
            using var output = new MemoryStream();
            var result = Subprocess.Run([program, "-version"], output, TimeSpan.FromSeconds(30));
            return result.ExitCode == 0 ? Encoding.UTF8.GetString(output.ToArray()).Split('\n')[0].TrimEnd() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
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

    // The jobs test's lookups, against the live app.
    static long MessageId(AttachmentsApp app, string clientMessageId) =>
        (long)app.Scalar($"SELECT id FROM messages WHERE client_message_id = '{clientMessageId}'")!;

    static long BlobId(AttachmentsApp app, string clientMessageId) =>
        (long)app.Scalar(
            $"SELECT b.id FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id WHERE a.record_type = 'Message' AND a.name = 'attachment' AND a.record_id = {MessageId(app, clientMessageId)}")!;

    static string Metadata(AttachmentsApp app, long blobId) =>
        (string)app.Scalar($"SELECT metadata FROM active_storage_blobs WHERE id = {blobId}")!;

    static bool Analyzed(AttachmentsApp app, long blobId) => Metadata(app, blobId).Contains("\"analyzed\":true", StringComparison.Ordinal);

    static bool BlobGone(AttachmentsApp app, long blobId) =>
        (long)app.Scalar($"SELECT COUNT(*) FROM active_storage_blobs WHERE id = {blobId}")! == 0;

    static string PathFor(AttachmentsApp app, long blobId) =>
        app.App.Storage!.Service.PathFor((string)app.Scalar($"SELECT key FROM active_storage_blobs WHERE id = {blobId}")!);

    // The same wait, reporting the job runner's log when it doesn't finish in time.
    static async Task UntilAsync(Func<bool> done, IReadOnlyList<string> logs)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (!done())
        {
            if (await Task.WhenAny(deadline, Task.Delay(20)) == deadline)
            {
                Assert.Fail($"timed out waiting for the job runner\n{string.Join('\n', logs)}");
            }
        }
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"gid://campfire/[^/]+/(\d+)")]
    private static partial Regex GidId();

    static string FirstDifference(string want, string have)
    {
        var at = Enumerable.Range(0, Math.Min(want.Length, have.Length)).TakeWhile(i => want[i] == have[i]).Count();
        return $"at {at}: …{Truncate(want, at)}…, expected …{Truncate(have, at)}…";
    }

    static string Truncate(string text, int at) => text[at..Math.Min(text.Length, at + 80)];

    public void Dispose() => app.Dispose();
}
