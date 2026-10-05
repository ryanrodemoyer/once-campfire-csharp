using System.Text.Json;

namespace Campfire.Vectors;

// vectors/opengraph/ and vectors/webhook/, written by reference-tools/oracle/{opengraph,webhook}.rb:
// scripted HTTP servers (cases.json) and what the reference's Opengraph fetcher and bot webhook
// client did against them (expected.json). Headers are [name, value] pairs in wire order.

public sealed record OpenGraphCasesFile(
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Hosts,
    IReadOnlyList<string> PublicIps,
    IReadOnlyList<OpenGraphRoute> Routes,
    IReadOnlyList<OpenGraphCaseUrl> Cases);

/// <summary>
/// A scripted response. <c>BodyRepeat</c> is [string, count]; <c>PadTo</c> pads the body to that
/// many bytes.
/// </summary>
public sealed record OpenGraphRoute(
    string Method,
    string Host,
    string Path,
    int Status,
    IReadOnlyList<IReadOnlyList<string>> Headers,
    string? Body = null,
    JsonElement? BodyRepeat = null,
    bool? Chunked = null,
    long? PadTo = null,
    bool? Gzip = null,
    string? BodyB64 = null);

public sealed record OpenGraphCaseUrl(string Name, string Url);

public sealed record OpenGraphExpectedCase(
    string Name,
    string Url,
    OpenGraphResponse Response,
    IReadOnlyList<string> Lookups,
    IReadOnlyList<IReadOnlyList<string>> Requests);

/// <summary>The unfurl endpoint's status and JSON body, or the exception the fetch raised.</summary>
public sealed record OpenGraphResponse(int Status, string? Body = null, string? Error = null);

public sealed record WebhookCase(
    string Name,
    int Status,
    IReadOnlyList<IReadOnlyList<string>> Headers,
    string? Body = null,
    string? BodyB64 = null,
    bool? Gzip = null,
    int? Delay = null,
    string? Url = null);

public sealed record WebhookExpectedCase(
    string Name,
    WebhookReply? Reply,
    IReadOnlyList<WebhookRequest> Requests,
    int? Status = null,
    string? Error = null);

/// <summary>The message the bot's reply became: text (base64, with its encoding) or an attachment.</summary>
public sealed record WebhookReply(
    string? TextB64 = null, string? Encoding = null, bool? Valid = null, WebhookAttachment? Attachment = null);

public sealed record WebhookAttachment(string Filename, string ContentType, string BodyB64);

public sealed record WebhookRequest(string RequestLine, IReadOnlyList<IReadOnlyList<string>> Headers, string Body);

public static class IntegrationVectors
{
    static readonly Lazy<OpenGraphCasesFile> OpenGraphCasesData = new(() => VectorFiles.Load<OpenGraphCasesFile>("opengraph/cases.json"));
    static readonly Lazy<IReadOnlyList<OpenGraphExpectedCase>> OpenGraphExpectedData = new(() => VectorFiles.Load<IReadOnlyList<OpenGraphExpectedCase>>("opengraph/expected.json"));
    static readonly Lazy<IReadOnlyList<WebhookCase>> WebhookCasesData = new(() => VectorFiles.Load<IReadOnlyList<WebhookCase>>("webhook/cases.json"));
    static readonly Lazy<IReadOnlyList<WebhookExpectedCase>> WebhookExpectedData = new(() => VectorFiles.Load<IReadOnlyList<WebhookExpectedCase>>("webhook/expected.json"));

    public static OpenGraphCasesFile OpenGraphCasesFile => OpenGraphCasesData.Value;
    public static IReadOnlyList<OpenGraphExpectedCase> OpenGraphExpectedFile => OpenGraphExpectedData.Value;
    public static IReadOnlyList<WebhookCase> WebhookCasesFile => WebhookCasesData.Value;
    public static IReadOnlyList<WebhookExpectedCase> WebhookExpectedFile => WebhookExpectedData.Value;

    public static TheoryData<OpenGraphExpectedCase> OpenGraph() => VectorFiles.Rows(OpenGraphExpectedFile, c => c.Name);
    public static TheoryData<WebhookExpectedCase> Webhook() => VectorFiles.Rows(WebhookExpectedFile, c => c.Name);
}
