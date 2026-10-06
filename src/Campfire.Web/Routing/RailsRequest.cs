using System.Text.RegularExpressions;
using Campfire.RailsCompat.Params;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Routing;

/// <summary>
/// What <c>ActionDispatch::Request</c> knows about a request once the router has seen it: the
/// method after <c>Rack::MethodOverride</c>, the matched route and <c>path_parameters</c>, the
/// parsed body, <c>params</c>, the negotiated <c>formats</c> and Turbo Frame detection. The router
/// installs it as a feature; read it with <see cref="RailsRequestExtensions.RailsRequest"/>.
/// </summary>
public sealed partial class RailsRequest : IDisposable
{
    ParamHash? queryParameters;
    ParamHash? parameters;
    IReadOnlyList<MimeType>? formats;

    public RailsRequest(HttpContext context)
    {
        Context = context;
        WireMethod = context.Request.Method;
        Method = WireMethod;
        OriginalPath = RawPath(context);
        Path = PathEscaping.NormalizePath(OriginalPath);
    }

    public HttpContext Context { get; }

    /// <summary>The method on the wire (<c>raw_request_method</c> before any override).</summary>
    public string WireMethod { get; }

    /// <summary><c>request.request_method</c>: the wire method, or a POST's <c>_method</c> override.</summary>
    public string Method { get; internal set; }

    /// <summary><c>PATH_INFO</c>: the request target's path, still percent-encoded.</summary>
    public string OriginalPath { get; }

    /// <summary>The path the router matches: <see cref="OriginalPath"/> normalized.</summary>
    public string Path { get; }

    /// <summary>The matched route, once routing has succeeded.</summary>
    public Route? Route { get; private set; }

    /// <summary><c>request.path_parameters</c> (empty until a route matches).</summary>
    public RouteParameters PathParameters { get; private set; } = new();

    /// <summary>The parsed request body (<c>request.request_parameters</c> and <c>raw_post</c>).</summary>
    public ParsedBody Body { get; internal set; } = ParsedBody.Empty;

    public bool IsHead => WireMethod == "HEAD";

    /// <summary><c>request.query_parameters</c>; throws <see cref="ParamException"/> for a malformed query.</summary>
    public ParamHash QueryParameters => queryParameters ??= ParamBuilder.FromQueryString(QueryString());

    /// <summary>
    /// <c>request.parameters</c> (<c>params</c>): body params, then query params, then path params,
    /// later ones winning. Throws the body's or the query's parse error, as Rails does.
    /// </summary>
    public ParamHash Parameters
    {
        get
        {
            if (parameters is null)
            {
                var merged = new ParamHash();
                merged.Merge(Body.Params);
                merged.Merge(QueryParameters);
                foreach (var (key, value) in PathParameters)
                {
                    merged[key] = value;
                }
                parameters = merged;
            }
            return parameters;
        }
    }

    /// <summary><c>request.xhr?</c>: <c>X-Requested-With</c> mentions XMLHttpRequest.</summary>
    public bool IsXhr => XmlHttpRequest().IsMatch(Context.Request.Headers["X-Requested-With"].ToString());

    /// <summary>turbo-rails' <c>turbo_frame_request_id</c>: the <c>Turbo-Frame</c> header.</summary>
    public string? TurboFrameRequestId => Context.Request.Headers.TryGetValue("Turbo-Frame", out var frame) ? frame.ToString() : null;

    /// <summary>turbo-rails' <c>turbo_frame_request?</c>: the <c>Turbo-Frame</c> header isn't blank.</summary>
    public bool IsTurboFrameRequest => !string.IsNullOrWhiteSpace(TurboFrameRequestId);

    /// <summary>
    /// <c>request.formats</c> (actionpack <c>http/mime_negotiation.rb</c>): <c>params[:format]</c>
    /// when there is one; else the Accept header unless it looks like a browser's (a <c>*/*</c>
    /// among other types); else the path's extension; else JS for XHR and HTML otherwise. Only
    /// registered types (and <c>*/*</c>) are kept.
    /// </summary>
    public IReadOnlyList<MimeType> Formats => formats ??= NegotiateFormats();

    /// <summary><c>request.format</c>: the first of <see cref="Formats"/>, or null (<c>Mime::NullType</c>).</summary>
    public MimeType? Format => Formats.Count > 0 ? Formats[0] : null;

    /// <summary>
    /// <c>request.content_mime_type</c>: the Content-Type up to its first <c>,</c> or <c>;</c>, or
    /// null. Throws <see cref="InvalidMimeTypeException"/> when it isn't a MIME type.
    /// </summary>
    public MimeType? ContentMimeType
    {
        get
        {
            var contentType = Context.Request.ContentType;
            if (contentType is null)
            {
                return null;
            }
            var end = contentType.AsSpan().IndexOfAny(',', ';');
            var type = (end < 0 ? contentType : contentType[..end]).Trim(' ', '\t', '\n', '\v', '\f', '\r', '\0').ToLowerInvariant();
            return MimeType.Lookup(type);
        }
    }

    /// <summary><c>request.accepts</c>: the parsed Accept header, or the content type when there is none.</summary>
    public IReadOnlyList<MimeType?> Accepts
    {
        get
        {
            var header = Context.Request.Headers.Accept.ToString().Trim(' ', '\t', '\n', '\v', '\f', '\r', '\0');
            return header.Length == 0 ? [ContentMimeType] : [.. MimeType.Parse(header)];
        }
    }

    /// <summary><c>respond_to</c>'s choice among <paramref name="order"/> (<c>negotiate_mime</c>).</summary>
    public MimeType? NegotiateMime(IReadOnlyList<MimeType> order)
    {
        foreach (var priority in Formats)
        {
            if (priority.IsAll)
            {
                return order.Count > 0 ? order[0] : null;
            }
            if (order.Contains(priority))
            {
                return priority;
            }
        }
        return order.Contains(MimeType.All) ? Format : null;
    }

    public void Dispose() => Body.Dispose();

    internal void Matched(RouteMatch match)
    {
        Route = match.Route;
        PathParameters = match.Parameters;
        parameters = null;
        formats = null;
    }

    MimeType[] NegotiateFormats()
    {
        List<MimeType?> candidates;
        if (FormatParameter() is { } format)
        {
            candidates = [MimeType.LookupByExtension(format)];
        }
        else if (HasValidAcceptHeader())
        {
            candidates = [.. Accepts];
        }
        else if (PathExtension().Match(OriginalPath) is { Success: true } extension)
        {
            candidates = [MimeType.LookupByExtension(extension.Groups[1].Value)];
        }
        else
        {
            candidates = [IsXhr ? MimeType.Js : MimeType.Html];
        }
        return candidates.Where(type => type is not null && (type.Symbol is not null || type.IsAll)).Select(type => type!).ToArray();
    }

    // params_readable?: parameters[:format], with a malformed body or query reading as no format.
    string? FormatParameter()
    {
        try
        {
            return Parameters["format"] switch
            {
                null => null,
                string text => text,
                var other => QueryStringValue(other),
            };
        }
        catch (Exception error) when (error is ParamException or BadRequestException)
        {
            return null;
        }
    }

    // valid_accept_header
    bool HasValidAcceptHeader()
    {
        var accept = Context.Request.Headers.Accept.ToString();
        var present = !string.IsNullOrWhiteSpace(accept);
        return (IsXhr && (present || ContentMimeType is not null)) || (present && !BrowserLikeAccepts().IsMatch(accept));
    }

    // Mime[...] looks a non-string format up by its to_s, which no extension matches.
    static string QueryStringValue(object value) => value.ToString() ?? "";

    string QueryString()
    {
        var query = Context.Request.QueryString.Value;
        return string.IsNullOrEmpty(query) ? "" : query[1..];
    }

    // Rack's PATH_INFO is the request target's path, still percent-encoded.
    static string RawPath(HttpContext context)
    {
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(target) || !target.StartsWith('/'))
        {
            return (context.Request.PathBase + context.Request.Path).ToUriComponent();
        }
        var query = target.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? target : target[..query];
    }

    [GeneratedRegex("XMLHttpRequest", RegexOptions.IgnoreCase)]
    private static partial Regex XmlHttpRequest();

    // BROWSER_LIKE_ACCEPTS
    [GeneratedRegex(@",\s*\*/\*|\*/\*\s*,")]
    private static partial Regex BrowserLikeAccepts();

    // format_from_path_extension
    [GeneratedRegex(@"\.(\w+)\z")]
    private static partial Regex PathExtension();
}

public static class RailsRequestExtensions
{
    /// <summary>The request's <see cref="Routing.RailsRequest"/>, which the router installs.</summary>
    public static RailsRequest RailsRequest(this HttpContext context) =>
        context.Features.Get<RailsRequest>() ?? throw new InvalidOperationException("The request didn't come through the router");
}
