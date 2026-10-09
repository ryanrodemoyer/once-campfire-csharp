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

namespace Campfire.Web.Tests.Controllers.UsersSidebars;

/// <summary>
/// Replays <c>Vectors/sidebars.json</c>, what the reference did for /users/me/sidebar across all seed
/// users, explicit user id, Turbo frame request, signed-out and non-HTML requests, through the router
/// and Users::SidebarsController on the same database, with CSRF protection on. Each request must
/// answer as the reference did (status, headers, cookies, normalized HTML body).
/// </summary>
public sealed partial class SidebarsReplayTests : IDisposable
{
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/UsersSidebars/Vectors/sidebars.json")))!;

    static readonly string[] ComparedHeaders = ["content-type", "location", "vary", "cache-control", "x-frame-options", "etag"];

    const string sessionId = "0123456789abcdef0123456789abcdef";

    static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly MessagesApp app = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    static IEnumerable<JsonNode> Cases() => Vectors["cases"]!.AsArray().Select(sample => sample!);

    [Fact]
    public async Task Each_request_answers_what_the_reference_did()
    {
        var failures = new List<string>();
        foreach (var sample in Cases())
        {
            var name = sample["name"]!.GetValue<string>();
            var actual = await SendAsync(sample["request"]!);

            failures.AddRange(CompareResponse(sample["response"]!, actual).Select(failure => $"{name}: {failure}"));
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void The_vectors_cover_the_card()
    {
        var cases = Cases().ToDictionary(sample => sample["name"]!.GetValue<string>());

        // All 10 seed users
        foreach (var name in new[]
        {
            "sidebar for David", "sidebar for Jason", "sidebar for Bender Bot", "sidebar for Kevin",
            "sidebar for JZ", "sidebar for Rita Lopez", "sidebar for Mallory Banned",
            "sidebar for Deploy Bot", "sidebar for Old Bot", "sidebar for Lonely Lou"
        })
        {
            Assert.True(cases.ContainsKey(name), name);
            Assert.Equal(200, cases[name]["response"]!["status"]!.GetValue<int>());
        }

        // Explicit user id
        Assert.Equal(200, cases["sidebar for David by explicit id"]["response"]!["status"]!.GetValue<int>());

        // Turbo frame request
        var frameCase = cases["sidebar in Turbo frame for David"];
        Assert.Equal(200, frameCase["response"]!["status"]!.GetValue<int>());
        Assert.DoesNotContain("<nav id=\"nav\">", frameCase["response"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);

        // Signed out
        var signedOut = cases["sidebar signed out"];
        Assert.Equal(302, signedOut["response"]!["status"]!.GetValue<int>());
        Assert.Equal("http://campfire.test/session/new", signedOut["response"]!["headers"]!["location"]!.GetValue<string>());

        // Non-HTML format
        var jsonCase = cases["sidebar as JSON"];
        Assert.Equal(406, jsonCase["response"]!["status"]!.GetValue<int>());
    }

    async Task<Response> SendAsync(JsonNode request)
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
        var bytes = Encoding.UTF8.GetBytes(request["body"]?.GetValue<string>() ?? "");
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
        var body = expected["body"]!.GetValue<string>();
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

    static string Normalize(string text) =>
        CsrfMeta().Replace(AuthenticityToken().Replace(text, "$1<token>\""), "$1<token>\"");

    static bool SameHeader(string name, string? want, string? have)
    {
        if (name == "content-type" && want is not null && want.EndsWith("charset=UTF-8", StringComparison.Ordinal))
        {
            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }
        return want == have;
    }

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
        if (data?["session_id"] is { } id && id.GetValue<string>() != sessionId)
        {
            data["session_id"] = "<new session>";
            if (data["_csrf_token"] is not null)
            {
                data["_csrf_token"] = "<new token>";
            }
        }
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

    [GeneratedRegex("(name=\"authenticity_token\" value=\")[^\"]*\"")]
    private static partial Regex AuthenticityToken();

    [GeneratedRegex("(<meta name=\"csrf-token\" content=\")[^\"]*\"")]
    private static partial Regex CsrfMeta();
}
