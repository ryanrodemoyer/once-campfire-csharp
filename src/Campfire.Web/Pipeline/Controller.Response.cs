using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.Web.Routing;

namespace Campfire.Web.Pipeline;

// Building the response: status, headers, body (render, head, redirect_to), formats.
public abstract partial class Controller
{
    /// <summary>The response body once something rendered; null means <c>response_body</c> is unset.</summary>
    ReadOnlyMemory<byte>? body;
    Func<Stream, CancellationToken, Task>? streamBody;
    bool inAction;

    /// <summary>The response status (<c>response.status</c>), 200 until something sets it.</summary>
    public int Status { get; set; } = 200;

    /// <summary><c>response.headers</c>, starting with <c>config.action_dispatch.default_headers</c>.</summary>
    public ResponseHeaders Headers { get; } = new();

    /// <summary>
    /// <c>performed?</c>: something rendered, redirected or sent a <c>head</c>. A before callback
    /// that performs halts the chain.
    /// </summary>
    public bool Performed => body is not null || streamBody is not null;

    /// <summary><c>response.location</c></summary>
    public string? Location
    {
        get => Headers["Location"];
        set => Headers["Location"] = value;
    }

    /// <summary>The <c>Content-Type</c> header as set so far.</summary>
    public string? ContentType => Headers["Content-Type"];

    /// <summary>The response body rendered so far (empty for a <c>head</c> or redirect).</summary>
    public ReadOnlyMemory<byte> Body => body ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// The controller's <c>formats</c>: <c>request.formats</c> once the action runs, and nothing
    /// while the before callbacks do (Rails sets them in <c>Rendering#process_action</c>, inside
    /// the callbacks), which is why a <c>head</c> from a before callback is <c>text/html</c>.
    /// </summary>
    public IReadOnlyList<MimeType>? Formats => inAction ? Request.Formats : null;

    /// <summary>
    /// <c>head status, location:, content_type:</c>: no body, and unless the status has no content,
    /// the content type without a charset (the first of <see cref="Formats"/>, else HTML).
    /// </summary>
    public void Head(int status, string? location = null, string? contentType = null)
    {
        EnsureNotPerformed();
        Status = status;
        if (location is not null)
        {
            Location = location;
        }
        if (IncludesContent(status) && MediaType() is null)
        {
            SetContentType(contentType ?? HeadFormat().Value, charset: false);
        }
        body = ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// <c>redirect_to location, status:, notice:, alert:, flash:, allow_other_host:</c>. A path gets
    /// the request's protocol and host; another host, a path-relative URL or a control character
    /// throws <see cref="UnsafeRedirectException"/> (<c>load_defaults 8.2</c> raises for all three).
    /// </summary>
    public void RedirectTo(
        string location,
        int status = 302,
        string? notice = null,
        string? alert = null,
        IReadOnlyDictionary<string, string>? flash = null,
        bool allowOtherHost = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (alert is not null)
        {
            Flash.Alert = alert;
        }
        if (notice is not null)
        {
            Flash.Notice = notice;
        }
        if (flash is not null)
        {
            foreach (var (key, value) in flash)
            {
                Flash[key] = value;
            }
        }
        EnsureNotPerformed();
        var url = ComputeRedirectLocation(location);
        if (IllegalHeaderCharacter().IsMatch(url))
        {
            throw new UnsafeRedirectException($"The redirect URL {url} contains one or more illegal HTTP header field character. Set of legal characters defined in https://datatracker.ietf.org/doc/html/rfc7230#section-3.2.6");
        }
        if (!allowOtherHost && !IsUrlHostAllowed(url))
        {
            throw new UnsafeRedirectException($"Unsafe redirect to {RubyInspect(Truncate(url, 100))}, pass allow_other_host: true to redirect anyway.");
        }
        Location = url;
        body = ReadOnlyMemory<byte>.Empty;
        Status = status;
    }

    /// <summary><c>redirect_back_or_to fallback</c>: the referer when it's on this host, else the fallback.</summary>
    public void RedirectBackOrTo(string fallbackLocation, int status = 302, string? notice = null, string? alert = null)
    {
        if (Referer is { } referer && IsUrlHostAllowed(referer))
        {
            RedirectTo(referer, status, notice, alert);
        }
        else
        {
            RedirectTo(fallbackLocation, status, notice, alert);
        }
    }

    /// <summary>
    /// <c>render</c> of a body already produced, as <paramref name="contentType"/>
    /// (<c>charset=utf-8</c> is added unless it names one). Sets <c>Vary: Accept</c> when the
    /// format came from the <c>Accept</c> header.
    /// </summary>
    public void Render(ReadOnlyMemory<byte> content, string contentType, int status = 200, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        EnsureNotPerformed();
        Status = status;
        if (location is not null)
        {
            Location = location;
        }
        if (MediaType() is null)
        {
            SetContentType(contentType, charset: true);
        }
        SetVaryHeader();
        body = content;
    }

    /// <summary><c>render</c> of a template or partial written straight into the body, as <paramref name="format"/>.</summary>
    public void Render(MimeType format, Action<IBufferWriter<byte>> write, int status = 200)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(write);
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        Render(buffer.WrittenMemory, format.Value, status);
    }

    /// <summary><c>render html:</c>/<c>plain:</c>/<c>json:</c> of text.</summary>
    public void Render(string content, string contentType, int status = 200) =>
        Render(Encoding.UTF8.GetBytes(content), contentType, status);

    /// <summary>
    /// A body sent as a stream (<c>send_file</c>, Active Storage's proxy), which neither
    /// <c>Rack::ETag</c> digests nor <c>Rack::ConditionalGet</c> empties. Headers are the caller's.
    /// </summary>
    public void SendStream(Func<Stream, CancellationToken, Task> write, int status = 200)
    {
        ArgumentNullException.ThrowIfNull(write);
        EnsureNotPerformed();
        Status = status;
        streamBody = write;
    }

    /// <summary>
    /// <c>respond_to do |format| ... end</c>: the first of <paramref name="offered"/> (in the
    /// block's order) the request accepts. Throws <see cref="UnknownFormatException"/> (406) when
    /// none is.
    /// </summary>
    public MimeType RespondTo(params MimeType[] offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var chosen = Request.NegotiateMime(offered) ?? throw new UnknownFormatException();
        return chosen.IsAll ? offered[0] : chosen;
    }

    /// <summary>
    /// <c>response.content_type = type</c>: the media type, with <c>charset=utf-8</c> when
    /// <paramref name="charset"/> is true and the type doesn't name one.
    /// </summary>
    public void SetContentType(string contentType, bool charset = true)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        var hasCharset = contentType.Contains("charset=", StringComparison.OrdinalIgnoreCase);
        Headers["Content-Type"] = charset && !hasCharset ? $"{contentType}; charset=utf-8" : contentType;
    }

    /// <summary>
    /// <c>default_render</c> for an action that rendered nothing (C# actions have no implicit
    /// template): <c>MissingExactTemplate</c> for an interactive browser request, else
    /// <c>head :no_content</c>.
    /// </summary>
    internal void DefaultRender()
    {
        if (Request.Method == "GET" && Request.Format == MimeType.Html && !Request.IsXhr)
        {
            throw new MissingExactTemplateException($"{GetType().Name}#{ActionName} is missing a template for request formats: {string.Join(',', Request.Formats.Select(format => format.Value))}");
        }
        Head(204);
    }

    internal void BeginAction() => inAction = true;

    /// <summary>A fresh response for a <c>rescue_from</c> handler.</summary>
    internal void ResetResponse()
    {
        body = null;
        streamBody = null;
        Status = 200;
        Headers.Clear();
        StartResponse();
    }

    // ActionDispatch::Response.create: config.action_dispatch.default_headers (load_defaults 7.1).
    void StartResponse()
    {
        Headers["X-Frame-Options"] = "SAMEORIGIN";
        Headers["X-XSS-Protection"] = "0";
        Headers["X-Content-Type-Options"] = "nosniff";
        Headers["X-Permitted-Cross-Domain-Policies"] = "none";
        Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    }

    void EnsureNotPerformed()
    {
        if (Performed)
        {
            throw new DoubleRenderException();
        }
    }

    // _set_vary_header
    void SetVaryHeader()
    {
        if (string.IsNullOrWhiteSpace(Headers["Vary"]) && ShouldApplyVaryHeader())
        {
            Headers["Vary"] = "Accept";
        }
    }

    // should_apply_vary_header?: no format parameter, and the format came from a usable Accept.
    bool ShouldApplyVaryHeader()
    {
        if (FormatParameterReadable())
        {
            return false;
        }
        var accept = Header("Accept");
        var present = !string.IsNullOrWhiteSpace(accept);
        if (Request.IsXhr && (present || HasContentMimeType()))
        {
            return true;
        }
        return present && !BrowserLikeAccepts().IsMatch(accept!);
    }

    bool FormatParameterReadable()
    {
        try
        {
            return Request.Parameters["format"] is { } format && format is not false;
        }
        catch (Exception error) when (error is RailsCompat.Params.ParamException or BadRequestException)
        {
            return false;
        }
    }

    bool HasContentMimeType()
    {
        try
        {
            return Request.ContentMimeType is not null;
        }
        catch (InvalidMimeTypeException)
        {
            return false;
        }
    }

    // `content_type || Mime[formats.first] || :html`
    MimeType HeadFormat()
    {
        if (Formats is { Count: > 0 } formats && !formats[0].IsAll && formats[0].Symbol is not null)
        {
            return formats[0];
        }
        return MimeType.Html;
    }

    string? MediaType()
    {
        var contentType = ContentType;
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return media.Length == 0 ? null : media;
    }

    static bool IncludesContent(int status) => status is not (>= 100 and <= 199 or 204 or 205 or 304);

    // _compute_redirect_to_location for a string.
    string ComputeRedirectLocation(string location)
    {
        string url;
        if (AbsoluteUrl().IsMatch(location))
        {
            url = location;
        }
        else
        {
            if (location.Length > 0 && !location.StartsWith('/') && !location.StartsWith('?'))
            {
                throw new UnsafeRedirectException($"Path relative URL redirect detected: {RubyInspect(location)}");
            }
            url = RequestUrl.Protocol + RequestUrl.HostWithPort + location;
        }
        return url.Replace("\0", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
    }

    // _url_host_allowed?: URI(url).host is the request's host, or there's no host and it's a
    // path; a URL Ruby's URI can't parse isn't allowed.
    bool IsUrlHostAllowed(string url)
    {
        if (InvalidUriCharacter().IsMatch(url))
        {
            return false;
        }
        var match = UrlHost().Match(url);
        return match.Success
            ? match.Groups["host"].Value == RequestUrl.Host
            : url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);
    }

    static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";

    static string RubyInspect(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // /\A([a-z][a-z\d\-+.]*:|\/\/).*/i
    [GeneratedRegex(@"\A(?:[a-z][a-z0-9\-+.]*:|//)", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrl();

    // ILLEGAL_HEADER_VALUE_REGEX
    [GeneratedRegex(@"[\x00-\x08\x0A-\x1F]")]
    private static partial Regex IllegalHeaderCharacter();

    [GeneratedRegex(@"\A(?:[a-zA-Z][a-zA-Z0-9+\-.]*:)?//(?:[^@/?#]*@)?(?<host>\[[^\]]*\]|[^:/?#]*)")]
    private static partial Regex UrlHost();

    [GeneratedRegex(@"[^A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]")]
    private static partial Regex InvalidUriCharacter();

    [GeneratedRegex(@",\s*\*/\*|\*/\*\s*,")]
    private static partial Regex BrowserLikeAccepts();
}
