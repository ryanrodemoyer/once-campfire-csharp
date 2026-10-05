namespace Campfire.RailsCompat.Params;

/// <summary><c>Rack::MethodOverride</c> (rack 3.2.6, <c>rack/method_override.rb</c>).</summary>
public static class MethodOverride
{
    /// <summary><c>Rack::MethodOverride::HTTP_METHODS</c>.</summary>
    public static readonly IReadOnlySet<string> HttpMethods =
        new HashSet<string>(["GET", "HEAD", "PUT", "POST", "DELETE", "OPTIONS", "PATCH", "LINK", "UNLINK"], StringComparer.Ordinal);

    /// <summary>
    /// The method a request is routed as: for a POST, the form's <c>_method</c> (or else the
    /// <c>X-HTTP-Method-Override</c> header), upcased, when it names a method Rack allows.
    /// </summary>
    /// <param name="method">The method on the wire.</param>
    /// <param name="contentType">The request's Content-Type.</param>
    /// <param name="body">The parsed body; only consulted for form data, as in Rack.</param>
    /// <param name="overrideHeader">The <c>X-HTTP-Method-Override</c> header, if any.</param>
    public static string Resolve(string method, string? contentType, ParsedBody? body, string? overrideHeader)
    {
        if (method != "POST")
        {
            return method;
        }
        var candidate = MethodOverrideParam(contentType, body) ?? overrideHeader;
        var upcased = candidate?.ToUpperInvariant() ?? "";
        return HttpMethods.Contains(upcased) ? upcased : method;
    }

    // req.POST["_method"] if req.form_data? || req.parseable_data?, with parse errors rescued.
    // A non-string value (_method[x]=...) never names a method once it's to_s'd.
    static string? MethodOverrideParam(string? contentType, ParsedBody? body)
    {
        var mediaType = RequestBody.MediaType(contentType);
        var formData = mediaType is null or "application/x-www-form-urlencoded" or "multipart/form-data" or "multipart/related" or "multipart/mixed";
        if (!formData || body is null || body.Error is not null)
        {
            return null;
        }
        return body.Params["_method"] as string;
    }
}
