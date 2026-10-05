using System.Text.Json.Serialization;

namespace Campfire.Vectors;

// vectors/campfire_routes.json, campfire_sessions.json and campfire_user_agents.json, written by
// reference-tools/campfire/{routes,session_cookies,user_agents}.rb.

public sealed record RoutesFile(IReadOnlyList<RouteCase> Routes, IReadOnlyList<RecognitionCase> Recognitions);

/// <summary>One line of <c>rails routes</c>: verb, path pattern, controller#action and defaults.</summary>
public sealed record RouteCase(string Verb, string Path, string Endpoint, IReadOnlyDictionary<string, string> Defaults);

/// <summary>A concrete request and what the router recognizes it as; a null endpoint is no match.</summary>
public sealed record RecognitionCase(
    string Verb, string Path, string? Endpoint, IReadOnlyDictionary<string, string> Params);

public sealed record SessionsFile(
    IReadOnlyList<SessionCookieCase> Sessions, IReadOnlyList<SignedBlobCase> Blobs, ForgedSessionCookie Forged);

public sealed record SessionCookieCase(
    long SessionId, long UserId, string UserName, string Token, string CookieValue, string CookieHeader);

public sealed record SignedBlobCase(long BlobId, string SignedId, string RedirectPath);

/// <summary>A session_token cookie signed with the wrong secret.</summary>
public sealed record ForgedSessionCookie(string CookieValue, string CookieHeader);

public sealed record UserAgentsFile(
    IReadOnlyList<UserAgentCase> UserAgents,
    IReadOnlyList<UserAgentVersionCase> Versions,
    IReadOnlyList<UserAgentComparisonCase> Comparisons);

/// <summary>What the useragent gem and ApplicationPlatform report for one User-Agent header.</summary>
public sealed record UserAgentCase(
    string? Ua,
    string? Browser,
    RubyResult<string?> Version,
    RubyResult<string?> Platform,
    RubyResult<string?> Os,
    bool Bot,
    RubyResult<bool> Mobile,
    ApplicationPlatformCase ApplicationPlatform,
    RubyResult<bool> Blocked);

public sealed record ApplicationPlatformCase(
    bool Ios,
    bool Android,
    bool Mac,
    RubyResult<bool> Chrome,
    RubyResult<bool> Firefox,
    RubyResult<bool> Safari,
    RubyResult<bool> Edge,
    bool AppleMessages,
    bool Mobile,
    bool Desktop,
    RubyResult<bool> Windows,
    RubyResult<string?> OperatingSystem,
    string? Browser);

/// <summary>UserAgent::Version.new(string): whether it is nil and its segments as tagged strings.</summary>
public sealed record UserAgentVersionCase(
    [property: JsonPropertyName("string")] string Text,
    bool Nil,
    IReadOnlyList<string> ToA);

public sealed record UserAgentComparisonCase(string A, string B, int Cmp, bool Lt, bool Eq);

public static class CampfireVectors
{
    static readonly Lazy<RoutesFile> RoutesData = new(() => VectorFiles.Load<RoutesFile>("campfire_routes.json"));
    static readonly Lazy<SessionsFile> SessionsData = new(() => VectorFiles.Load<SessionsFile>("campfire_sessions.json"));
    static readonly Lazy<UserAgentsFile> UserAgentsData = new(() => VectorFiles.Load<UserAgentsFile>("campfire_user_agents.json"));

    public static RoutesFile RoutesFile => RoutesData.Value;
    public static SessionsFile SessionsFile => SessionsData.Value;
    public static UserAgentsFile UserAgentsFile => UserAgentsData.Value;

    public static TheoryData<RouteCase> Routes() => VectorFiles.Rows(RoutesFile.Routes, c => $"{c.Verb} {c.Path}");
    public static TheoryData<RecognitionCase> Recognitions() => VectorFiles.Rows(RoutesFile.Recognitions, c => $"{c.Verb} {c.Path}");
    public static TheoryData<SessionCookieCase> Sessions() => VectorFiles.Rows(SessionsFile.Sessions, c => c.UserName);
    public static TheoryData<SignedBlobCase> Blobs() => VectorFiles.Rows(SessionsFile.Blobs, c => c.BlobId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static TheoryData<UserAgentCase> UserAgents() => VectorFiles.Rows(UserAgentsFile.UserAgents);
    public static TheoryData<UserAgentVersionCase> UserAgentVersions() => VectorFiles.Rows(UserAgentsFile.Versions, c => $"\"{c.Text}\"");
    public static TheoryData<UserAgentComparisonCase> UserAgentComparisons() => VectorFiles.Rows(UserAgentsFile.Comparisons, c => $"{c.A} <=> {c.B}");
}
