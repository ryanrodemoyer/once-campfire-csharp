using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Cookies;
using Campfire.Vectors;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// Replays <c>Vectors/pages.json</c>, what the reference answered for messages#index and
/// rooms/refreshes#show (see <c>Vectors/index.rb</c>), through the router and those controllers
/// on the same database, in the same order. Each request must answer as the reference did:
/// status, headers, cookies and the body, with only CSRF tokens normalized.
/// </summary>
public sealed partial class IndexControllerTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/pages.json")))!;

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag"];

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    [Fact]
    public async Task Each_response_answers_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            var request = sample["request"]!;
            var actual = await app.SendAsync(
                request["method"]!.GetValue<string>(),
                request["path"]!.GetValue<string>(),
                request["headers"]!.AsObject().ToDictionary(header => header.Key, header => header.Value!.GetValue<string>()));
            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        var cases = Cases().ToDictionary(sample => sample["name"]!.GetValue<string>(), sample => sample!);

        // "/rooms/<id>/messages?before=<busy_060> equals the reference": All Talk, busy_060.
        var acceptance = cases["page before busy_060"];
        Assert.Equal("/rooms/486777696/messages?before=933434569", acceptance["request"]!["path"]!.GetValue<string>());
        var page = acceptance["response"]!;
        Assert.Equal(200, page["status"]!.GetValue<int>());
        var body = page["body"]!.GetValue<string>();
        var ids = MessageIds(body);
        Assert.Equal(40, ids.Count);
        Assert.DoesNotContain("933434569", ids);
        // fresh_when's etag, not Rack's digest of the body, so the replay compares it exactly.
        var etag = page["headers"]!["etag"]!.GetValue<string>();
        Assert.NotEqual(BodyEtag(body), etag);
        Assert.Matches(FreshWhenEtag(), etag);

        Assert.Equal(204, cases["an empty room"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(204, cases["an empty room as JSON"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(204, cases["a page before the first message"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(204, cases["a page after the last message"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, cases["before a message from another room"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, cases["before a missing message"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, cases["before a message id that isn't a number"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, cases["a room the member isn't in"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(404, cases["messages with no room"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(302, cases["messages, signed out"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(406, cases["messages as JSON"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(304, cases["page before busy_060, fresh"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(200, cases["page before busy_060, stale"]["response"]!["status"]!.GetValue<int>());

        var before = Normalize(body);
        Assert.Equal(before, Normalize(cases["before and after, so before wins"]["response"]!["body"]!.GetValue<string>()));
        Assert.Equal(
            Normalize(cases["the last page"]["response"]!["body"]!.GetValue<string>()),
            Normalize(cases["a blank before, so the last page"]["response"]!["body"]!.GetValue<string>()));
        var after = cases["a page after busy_060"]["response"]!["body"]!.GetValue<string>();
        Assert.DoesNotContain("data-message-id=\"933434569\"", after, StringComparison.Ordinal);
        Assert.Contains("data-message-id=\"", after, StringComparison.Ordinal);
        Assert.Contains("data-message-id=\"", cases["the last page"]["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);

        // rooms/refreshes_controller_test: new messages are appended, updated ones replaced.
        var refresh = cases["refresh with new and updated messages"]["response"]!["body"]!.GetValue<string>();
        Assert.Contains("turbo-stream action=\"append\"", refresh, StringComparison.Ordinal);
        Assert.Contains("turbo-stream action=\"replace\"", refresh, StringComparison.Ordinal);
        Assert.Contains("data-message-id=\"933434507\"", refresh, StringComparison.Ordinal);
        Assert.Equal(200, cases["refresh of an empty room"]["response"]!["status"]!.GetValue<int>());
        Assert.Equal(406, cases["refresh as HTML"]["response"]!["status"]!.GetValue<int>());
    }

    static List<string> MessageIds(string body) => [.. MessageId().Matches(body).Select(match => match.Groups[1].Value)];

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
            var want = headers[name]?.GetValue<string>();
            // Rack::ETag hashes the body. CSRF tokens make that hash differ on every render, so
            // skip it only when the reference's etag is that digest. fresh_when's etag is not, and
            // is compared exactly.
            if (name == "etag" && wantBody != body && IsBodyEtag(want, body))
            {
                continue;
            }
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
        foreach (var failure in CompareCookies(wantCookies, [.. actual.Headers.SetCookie.Select(cookie => cookie!)], wantBody != body))
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
        // Rails' error pages (PublicExceptions) spell the charset "UTF-8"; W01's ErrorPages
        // writes "utf-8" (reported on #22).
        if (name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal))
        {
            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }
        return want == have;
    }

    // Rack::ETag's weak SHA-256 of the raw body: 32 hex digits here, the full digest in Rack.
    static bool IsBodyEtag(string? etag, string body)
    {
        if (etag is null)
        {
            return false;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return etag == BodyEtag(hash) || etag == $"W/\"{hash}\"";
    }

    static string BodyEtag(string bodyOrHash)
    {
        var hash = bodyOrHash.Length == 64 && bodyOrHash.All(Uri.IsHexDigit)
            ? bodyOrHash
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bodyOrHash)));
        return $"W/\"{hash[..32]}\"";
    }

    // CSRF tokens are masked with random bytes, so they read as placeholders.
    static string Normalize(string text) =>
        CsrfMeta().Replace(AuthenticityToken().Replace(text, "$1<token>\""), "$1<token>\"");

    IEnumerable<string> CompareCookies(List<string> want, List<string> have, bool csrfNormalized)
    {
        var wantNames = want.Select(CookieName).Order().ToList();
        var haveNames = have.Select(CookieName).Order().ToList();
        if (!wantNames.SequenceEqual(haveNames))
        {
            // Fragment caching is unimplemented (M02). Rails' second render of a message is a cache
            // hit, so it never calls form_authenticity_token and doesn't write the session. Rendering
            // the partials again stores a new CSRF token and sets _campfire_session. The first render
            // of each page still has to match the reference's cookies exactly.
            var extraSession = csrfNormalized
                && haveNames.Count == wantNames.Count + 1
                && !wantNames.Contains("_campfire_session")
                && haveNames.Where(name => name != "_campfire_session").SequenceEqual(wantNames);
            if (!extraSession)
            {
                yield return $"cookies [{string.Join(", ", haveNames)}], expected [{string.Join(", ", wantNames)}]";
                yield break;
            }
            have = [.. have.Where(cookie => CookieName(cookie) != "_campfire_session")];
        }
        foreach (var expected in want)
        {
            var name = CookieName(expected);
            var actual = have.Single(cookie => CookieName(cookie) == name);
            if (name == "_campfire_session")
            {
                // Encrypted with a random IV: compare what it holds, and its attributes. The
                // session id and CSRF token are random too.
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

    public void Dispose() => app.Dispose();

    [GeneratedRegex("data-message-id=\"(\\d+)\"")]
    private static partial Regex MessageId();

    [GeneratedRegex("^W/\"[0-9a-f]{32}\"$")]
    private static partial Regex FreshWhenEtag();

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();
}
