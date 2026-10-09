using System.Text.RegularExpressions;

namespace Campfire.Jobs.Webhooks;

/// <summary><c>Mime::Type::InvalidMimeType</c>: a reply's content type that isn't a MIME type.</summary>
public sealed class InvalidMimeTypeException(string mimeType) : Exception($"{RubyInspect(mimeType)} is not a valid MIME type")
{
    static string RubyInspect(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

/// <summary>
/// A <c>Mime::Type</c> as <c>Mime::Type.lookup</c> returns it: <c>symbol</c> (null for an
/// unregistered type) and <c>to_s</c>, which for a synonym is the registered type, not the one asked for.
/// </summary>
public sealed record WebhookMimeType(string? Symbol, string MediaType);

/// <summary>
/// <c>Mime::Type.lookup</c> (actionpack <c>action_dispatch/http/mime_type.rb</c>) over the
/// reference app's registrations: Action Dispatch's (<c>mime_types.rb</c>) plus turbo-rails'
/// <c>turbo_stream</c>.
/// </summary>
public static partial class WebhookMimeTypes
{
    // `Mime::LOOKUP`: each registered string and synonym, with the type it finds.
    static readonly Dictionary<string, WebhookMimeType> Lookup = Registrations();

    /// <summary>
    /// A registered type by its exact string, else by the string up to any <c>;</c> with trailing
    /// whitespace stripped, else a new unregistered type, which must match <c>MIME_REGEXP</c>.
    /// Case matters: <c>IMAGE/PNG</c> is unregistered.
    /// </summary>
    public static WebhookMimeType Find(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Lookup.TryGetValue(value, out var registered))
        {
            return registered;
        }
        var mediaType = value.Split(';')[0].TrimEnd(' ', '\t', '\n', '\v', '\f', '\r', '\0');
        if (Lookup.TryGetValue(mediaType, out registered))
        {
            return registered;
        }
        return MimeRegexp().IsMatch(mediaType) ? new WebhookMimeType(null, mediaType) : throw new InvalidMimeTypeException(mediaType);
    }

    // `Mime::Type::MIME_REGEXP`. In the Ruby source "\s" inside the double-quoted parameter
    // pattern is a literal space.
    [GeneratedRegex("""\A(?:\*/\*|[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}/(?:\*|[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126})(?: *; *[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}(?:=(?:[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}|"[^"\r\\]*"))?)*[ \t\n\v\f\r]*)\z""", RegexOptions.CultureInvariant)]
    private static partial Regex MimeRegexp();

    static Dictionary<string, WebhookMimeType> Registrations()
    {
        var lookup = new Dictionary<string, WebhookMimeType>(StringComparer.Ordinal);
        void Register(string type, string symbol, params string[] synonyms)
        {
            var mime = new WebhookMimeType(symbol, type);
            foreach (var key in synonyms.Prepend(type))
            {
                lookup[key] = mime;
            }
        }

        Register("text/html", "html", "application/xhtml+xml");
        Register("text/plain", "text");
        Register("text/javascript", "js", "application/javascript", "application/x-javascript");
        Register("text/css", "css");
        Register("text/calendar", "ics");
        Register("text/csv", "csv");
        Register("text/vcard", "vcf");
        Register("text/vtt", "vtt", "vtt");
        Register("text/markdown", "md");
        Register("image/png", "png");
        Register("image/jpeg", "jpeg");
        Register("image/gif", "gif");
        Register("image/bmp", "bmp");
        Register("image/tiff", "tiff");
        Register("image/svg+xml", "svg");
        Register("image/webp", "webp");
        Register("video/mpeg", "mpeg");
        Register("audio/mpeg", "mp3");
        Register("audio/ogg", "ogg");
        Register("audio/aac", "m4a", "audio/mp4");
        Register("video/webm", "webm");
        Register("video/mp4", "mp4");
        Register("font/otf", "otf");
        Register("font/ttf", "ttf");
        Register("font/woff", "woff");
        Register("font/woff2", "woff2");
        Register("application/xml", "xml", "text/xml", "application/x-xml");
        Register("application/rss+xml", "rss");
        Register("application/atom+xml", "atom");
        Register("application/x-yaml", "yaml", "text/yaml");
        Register("multipart/form-data", "multipart_form");
        Register("application/x-www-form-urlencoded", "url_encoded_form");
        Register("application/json", "json", "text/x-json", "application/jsonrequest", "application/problem+json");
        Register("application/pdf", "pdf");
        Register("application/zip", "zip");
        Register("application/gzip", "gzip", "application/x-gzip");
        Register("text/vnd.turbo-stream.html", "turbo_stream");
        return lookup;
    }
}
