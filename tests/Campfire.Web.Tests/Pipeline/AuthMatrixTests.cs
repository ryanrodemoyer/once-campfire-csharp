using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests.Pipeline;

/// <summary>
/// Replays <c>Vectors/auth_matrix.json</c>, what the reference answered at the edges of
/// ApplicationController's chain (see <c>Vectors/generate.rb</c>), through the router and the C#
/// pipeline on the same database, in the same order, with CSRF protection on. The controllers are
/// the reference's, declared with the same callbacks; their actions are only as much as the
/// matrix reaches.
/// </summary>
public sealed class AuthMatrixTests : IDisposable
{
    static readonly JsonNode Matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Pipeline/Vectors/auth_matrix.json")))!;

    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-web-tests", Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;
    readonly WebApp app;
    readonly KeyGenerator keys = new(ParityEnvironment("SECRET_KEY_BASE")!);
    readonly DateTimeOffset now = DateTimeOffset.Parse(Matrix["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

    public AuthMatrixTests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "production.sqlite3");
        File.Copy(Path.Combine(VectorFiles.Root, "tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3"), path);
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            foreach (var fixture in Matrix["fixtures"]!.AsArray())
            {
                using var command = connection.CreateCommand();
                command.CommandText = fixture!.GetValue<string>();
                command.ExecuteNonQuery();
            }
        }
        var clock = new FixedClock(now);
        database = SqliteDatabase.Open(new SqliteDatabaseOptions(path) { Clock = clock, Readers = 2 });
        var (version, revision) = WebApp.VersionFrom(ParityEnvironment);
        app = new WebApp
        {
            Database = database,
            Keys = keys,
            Clock = clock,
            AppVersion = version,
            GitRevision = revision,
            Router = new Router(MatrixControllers.Table, ErrorPages.FromDirectory(Path.Combine(VectorFiles.Root, "reference/public"))),
        };
    }

    static IEnumerable<string> CaseNames() => Matrix["cases"]!.AsArray().Select(sample => sample!["name"]!.GetValue<string>());

    [Fact]
    public async Task The_chain_answers_like_the_reference()
    {
        var failures = new List<string>();
        foreach (var sample in Matrix["cases"]!.AsArray())
        {
            var name = sample!["name"]!.GetValue<string>();
            var actual = await SendAsync(sample["request"]!);
            failures.AddRange(Compare(sample["request"]!, sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_matrix_covers_the_card()
    {
        var names = CaseNames().ToList();
        Assert.Contains("no cookie", names);
        Assert.Contains("expired session_token", names);
        Assert.Contains("bot key on a non-bot route", names);
        Assert.Contains("write, bad token", names);
        Assert.Contains("banned IP write", names);
        Assert.Contains("member write", names);
    }

    async Task<Response> SendAsync(JsonNode request)
    {
        var context = new DefaultHttpContext();
        var method = request["method"]!.GetValue<string>();
        var target = request["path"]!.GetValue<string>();
        var query = target.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? target : target[..query];
        context.Request.Method = method;
        context.Request.Path = PathString.FromUriComponent(path);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        foreach (var (header, value) in request["headers"]!.AsObject())
        {
            context.Request.Headers[header] = value!.GetValue<string>();
        }
        var body = request["body"]?.GetValue<string>();
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await app.HandleAsync(context);
        var received = responseBody.ToArray();
        if (context.Response.Headers.ContentEncoding == "gzip" && received.Length > 0)
        {
            using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(received), System.IO.Compression.CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            gzip.CopyTo(decoded);
            received = decoded.ToArray();
        }
        return new Response(context.Response.StatusCode, context.Response.Headers, received);
    }

    IEnumerable<string> Compare(JsonNode request, JsonNode expected, Response actual)
    {
        var status = expected["status"]!.GetValue<int>();
        if (status != actual.Status)
        {
            yield return $"status {actual.Status}, expected {status}";
        }
        var headers = expected["headers"]!.AsObject();
        foreach (var name in Headers)
        {
            // The stand-in page's body (and so Rack::ETag's digest) isn't the reference's.
            if (status == 200 && name == "etag")
            {
                continue;
            }
            var want = headers[name]?.GetValue<string>();
            var have = actual.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
            if (!SameHeader(name, want, have))
            {
                yield return $"{name} {Show(have)}, expected {Show(want)}";
            }
        }
        foreach (var failure in CompareCookies(request, expected["set_cookies"]!.AsArray().Select(cookie => cookie!.GetValue<string>()).ToList(), actual.Headers.SetCookie.Select(cookie => cookie!).ToList()))
        {
            yield return failure;
        }
        // The incompatible browser page is A01's; the stub only stands in for its status and cookies.
        if (status != 200)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(actual.Body));
            if (sha != expected["body_sha256"]!.GetValue<string>())
            {
                yield return $"body of {actual.Body.Length} bytes differs (expected {expected["body_length"]})";
            }
        }
    }

    static bool SameHeader(string name, string? want, string? have)
    {
        // Rails' error pages (PublicExceptions) spell the charset "UTF-8"; W01's ErrorPages
        // writes "utf-8" (reported on #22). Header values are otherwise compared exactly.
        if (name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal))
        {
            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }
        return want == have;
    }

    IEnumerable<string> CompareCookies(JsonNode request, List<string> want, List<string> have)
    {
        var wantNames = want.Select(CookieName).ToList();
        var haveNames = have.Select(CookieName).ToList();
        if (!wantNames.Order().SequenceEqual(haveNames.Order()))
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
                var (wantData, haveData) = (SessionData(expected, request), SessionData(actual, request));
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

    // The session the cookie holds. An id or CSRF token the server made up (the request had no
    // session) is random on both sides, so it reads as "new".
    JsonObject? SessionData(string setCookie, JsonNode request)
    {
        var value = setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
        var data = CookieJar.FromHeader(value, keys, () => now).Encrypted.Get("_campfire_session") as JsonObject;
        var hadSession = (request["headers"]!["Cookie"]?.GetValue<string>() ?? "").Contains("_campfire_session", StringComparison.Ordinal);
        if (data is not null && !hadSession)
        {
            foreach (var key in (string[])["session_id", "_csrf_token"])
            {
                if (data.ContainsKey(key))
                {
                    data[key] = "new";
                }
            }
        }
        return data;
    }

    static string CookieName(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    static string Attributes(string setCookie) => setCookie[setCookie.IndexOf(';', StringComparison.Ordinal)..];

    static string Show(string? value) => value is null ? "(none)" : $"\"{value}\"";

    static readonly string[] Headers = ["location", "content-type", "content-encoding", "cache-control", "vary", "x-version", "x-rev", "x-frame-options", "x-xss-protection",
        "x-content-type-options", "x-permitted-cross-domain-policies", "referrer-policy", "etag", "last-modified"];

    static string? ParityEnvironment(string name)
    {
        foreach (var line in File.ReadLines(Path.Combine(VectorFiles.Root, "parity/.env.reference")))
        {
            if (line.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return line[(name.Length + 1)..];
            }
        }
        return null;
    }

    public void Dispose()
    {
        database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    sealed record Response(int Status, IHeaderDictionary Headers, byte[] Body);
}

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
