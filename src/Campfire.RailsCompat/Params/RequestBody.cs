using System.Text.RegularExpressions;

namespace Campfire.RailsCompat.Params;

/// <summary>
/// A request body as <c>ActionDispatch::Request#POST</c> reads it: the raw bytes (empty for
/// multipart, whose files went to disk) and the params, or the error Rails raises when they're
/// read. Disposing it deletes the uploaded temp files.
/// </summary>
public sealed class ParsedBody : IDisposable
{
    readonly ParamHash? parameters;

    internal ParsedBody(byte[] raw, ParamHash? parameters, ParamException? error, IReadOnlyList<UploadedFile> files)
    {
        Raw = raw;
        this.parameters = parameters;
        Error = error;
        Files = files;
    }

    public static ParsedBody Empty { get; } = new([], new ParamHash(), null, []);

    /// <summary><c>request.raw_post</c>, except for multipart bodies, which aren't kept.</summary>
    public byte[] Raw { get; }

    /// <summary>Why the params couldn't be parsed; Rails raises it when they're first read.</summary>
    public ParamException? Error { get; }

    public IReadOnlyList<UploadedFile> Files { get; }

    /// <summary><c>request.request_parameters</c>; throws <see cref="Error"/> if there was one.</summary>
    public ParamHash Params => parameters ?? throw Error!;

    public void Dispose()
    {
        foreach (var file in Files)
        {
            file.Dispose();
        }
    }
}

/// <summary>
/// Reads request bodies into params the way <c>ActionDispatch::Request#POST</c> does
/// (<c>action_dispatch/http/request.rb</c>), on top of <c>Rack::Request#form_pairs</c>.
/// </summary>
public static partial class RequestBody
{
    /// <summary>
    /// The most of a non-multipart body read into memory. Rails reads any size; beyond this is a
    /// <see cref="RequestBodyTooLargeException"/>. Forms are already limited to 4 MB by Rack.
    /// </summary>
    public const int MaxBufferedBody = 16 * 1024 * 1024;

    static readonly string[] FormDataMediaTypes = ["application/x-www-form-urlencoded", "multipart/form-data"];
    static readonly string[] ParseableDataMediaTypes = ["multipart/related", "multipart/mixed"];

    // Mime[:json] and its synonyms (action_dispatch/http/mime_types.rb).
    static readonly string[] JsonMimeTypes = ["application/json", "text/x-json", "application/jsonrequest", "application/problem+json"];

    /// <summary>
    /// Reads and parses <paramref name="body"/>. <paramref name="method"/> is the method on the wire,
    /// before any <c>_method</c> override (Rack's <c>form_data?</c> treats a POST without a
    /// Content-Type as a form). Parameter errors are returned in <see cref="ParsedBody.Error"/>, as
    /// Rails raises them lazily; an oversized body throws <see cref="RequestBodyTooLargeException"/>.
    /// </summary>
    public static async Task<ParsedBody> ParseAsync(
        string method,
        string? contentType,
        long? contentLength,
        Stream body,
        string? tempDirectory = null,
        int maxBufferedBody = MaxBufferedBody,
        CancellationToken cancellationToken = default)
    {
        var mediaType = MediaType(contentType);
        var isForm = (method == "POST" && mediaType is null) || FormDataMediaTypes.Contains(mediaType) || ParseableDataMediaTypes.Contains(mediaType);
        try
        {
            // parse_formatted_parameters: a registered parser for the Content-Type, unless the body
            // is empty; otherwise Rack's form pairs.
            if (contentLength != 0 && IsJson(contentType))
            {
                var json = await ReadAllAsync(body, maxBufferedBody, tooLarge: true, cancellationToken).ConfigureAwait(false);
                if (json.Length > 0)
                {
                    return new ParsedBody(json, ParamBuilder.FromJson(json), null, []);
                }
                return ParsedBody.Empty;
            }
            if (!isForm)
            {
                var raw = await ReadAllAsync(body, maxBufferedBody, tooLarge: true, cancellationToken).ConfigureAwait(false);
                return new ParsedBody(raw, new ParamHash(), null, []);
            }

            var pairs = await MultipartParser.ParseAsync(body, contentLength, contentType, tempDirectory, cancellationToken).ConfigureAwait(false);
            if (pairs is not null)
            {
                return FromPairs([], pairs);
            }
            // Rack reads at most bytesize_limit + 2 bytes of a form: enough to tell it's too big.
            var form = await ReadAllAsync(body, QueryParser.FormByteSizeLimit + 2, tooLarge: false, cancellationToken).ConfigureAwait(false);
            return FromPairs(form, QueryParser.FormPairs(form));
        }
        catch (ParamException error)
        {
            return new ParsedBody([], null, error, []);
        }
    }

    static ParsedBody FromPairs(byte[] raw, List<ParamPair> pairs)
    {
        var files = pairs.Select(pair => pair.Value).OfType<UploadedFile>().ToList();
        try
        {
            return new ParsedBody(raw, ParamBuilder.FromPairs(pairs), null, files);
        }
        catch (ParamException error)
        {
            return new ParsedBody(raw, null, error, files);
        }
    }

    /// <summary><c>Rack::MediaType.type</c>: the Content-Type before any <c>;</c> or <c>,</c>, downcased.</summary>
    public static string? MediaType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }
        var end = contentType.AsSpan().IndexOfAny(';', ',');
        var type = end < 0 ? contentType : contentType[..end];
        return type.TrimEnd(RubyWhitespace).ToLowerInvariant();
    }

    // content_mime_type == Mime[:json]. A Content-Type Rails can't parse as a MIME type is a 406
    // (Mime::Type::InvalidMimeType).
    static bool IsJson(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }
        var type = ContentMimeType().Match(contentType).Groups[1].Value.Trim(RubyWhitespace).ToLowerInvariant();
        if (JsonMimeTypes.Contains(type))
        {
            return true;
        }
        if (!MimeType().IsMatch(type))
        {
            throw new ParamException(ParamErrorKind.InvalidMimeType, $"\"{type}\" is not a valid MIME type");
        }
        return false;
    }

    // Reads the body, up to limit bytes: past that, either stop there or throw.
    static async Task<byte[]> ReadAllAsync(Stream body, int limit, bool tooLarge, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < limit)
        {
            var read = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            buffer.Write(chunk, 0, read);
        }
        if (tooLarge && await body.ReadAsync(chunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new RequestBodyTooLargeException();
        }
        return buffer.ToArray();
    }

    static readonly char[] RubyWhitespace = ['\0', '\t', '\n', '\v', '\f', '\r', ' '];

    // /^([^,;]*)/
    [GeneratedRegex("^([^,;]*)", RegexOptions.Multiline)]
    private static partial Regex ContentMimeType();

    // Mime::Type::MIME_REGEXP
    [GeneratedRegex("""\A(?:\*/\*|[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}/(?:\*|[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126})(?>[ \t\r\n\f\v]*;[ \t\r\n\f\v]*[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}(?:=(?:[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}|"[^"\r\\]*"))?)*[ \t\r\n\f\v]*)\z""")]
    private static partial Regex MimeType();
}
