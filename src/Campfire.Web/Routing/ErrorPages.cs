using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Routing;

/// <summary>
/// <c>ActionDispatch::ShowExceptions</c> with <c>ActionDispatch::PublicExceptions</c> (actionpack
/// <c>middleware/show_exceptions.rb</c>, <c>middleware/public_exceptions.rb</c>): an exception's
/// status, and a body in the request's format: <c>{status, error}</c> as JSON or XML, else
/// <c>public/&lt;status&gt;.html</c>, else nothing. HEAD gets the headers only.
/// </summary>
public sealed class ErrorPages(Func<int, byte[]?> publicPage)
{
    /// <summary>Error pages read from a <c>public/</c> directory (the reference's, in tests).</summary>
    public static ErrorPages FromDirectory(string publicDirectory) => new(status =>
    {
        var path = System.IO.Path.Combine(publicDirectory, $"{status}.html");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    });

    /// <summary>The response Rails sends for <paramref name="exception"/>.</summary>
    public ErrorResponse For(Exception exception, RailsRequest request) => For(HttpErrors.StatusFor(exception), request);

    /// <summary>The response <c>PublicExceptions</c> renders for <paramref name="status"/>.</summary>
    public ErrorResponse For(int status, RailsRequest request)
    {
        var format = ErrorFormat(request);
        if (request.IsHead)
        {
            return Render(status, format?.Value ?? "", []);
        }
        var body = new ErrorBody(status, StatusNames.TryGetValue(status, out var name) ? name : StatusNames[500]);
        if (format == MimeType.Json)
        {
            return Render(status, format.Value, JsonSerializer.SerializeToUtf8Bytes(body, ErrorBodyJsonContext.Default.ErrorBody));
        }
        if (format == MimeType.Xml)
        {
            return Render(status, format.Value, Encoding.UTF8.GetBytes(body.ToXml()));
        }
        // render_html: public/<status>.html, or the empty X-Cascade response ShowExceptions turns
        // into an empty text/html page.
        return Render(status, "text/html", publicPage(status) ?? []);
    }

    public async Task WriteAsync(HttpContext context, Exception exception)
    {
        var response = For(exception, context.RailsRequest());
        context.Response.Clear();
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength = response.ContentLength;
        if (!context.RailsRequest().IsHead)
        {
            await context.Response.Body.WriteAsync(response.Body).ConfigureAwait(false);
        }
    }

    static ErrorResponse Render(int status, string contentType, byte[] body) =>
        new(status, $"{contentType}; charset=utf-8", body.Length, body);

    // request.formats.first on the error request, which falls back to HTML when the Accept or
    // Content-Type header isn't a valid MIME type (fallback_to_html_format_if_invalid_mime_type).
    static MimeType? ErrorFormat(RailsRequest request)
    {
        try
        {
            return request.Format;
        }
        catch (InvalidMimeTypeException)
        {
            return MimeType.Html;
        }
    }

    // Rack::Utils::HTTP_STATUS_CODES (rack 3.2.6) for the statuses the port answers with.
    static readonly Dictionary<int, string> StatusNames = new()
    {
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [403] = "Forbidden",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [406] = "Not Acceptable",
        [409] = "Conflict",
        [413] = "Content Too Large",
        [422] = "Unprocessable Content",
        [429] = "Too Many Requests",
        [500] = "Internal Server Error",
        [501] = "Not Implemented",
    };
}

/// <summary>An error response: status, <c>Content-Type</c>, <c>Content-Length</c> and body (empty for HEAD).</summary>
public sealed record ErrorResponse(int Status, string ContentType, long ContentLength, byte[] Body);

/// <summary><c>{ status:, error: }</c>, the body <c>PublicExceptions</c> renders for JSON and XML.</summary>
public sealed record ErrorBody(int Status, string Error)
{
    // Hash#to_xml (activesupport core_ext/hash/conversions.rb) over Builder 3.3.
    public string ToXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<hash>\n" +
        $"  <status type=\"integer\">{Status}</status>\n" +
        $"  <error>{System.Security.SecurityElement.Escape(Error)}</error>\n</hash>\n";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(ErrorBody))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ErrorBodyJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
