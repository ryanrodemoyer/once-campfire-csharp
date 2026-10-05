using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Assets;

// The parts of a request ActionDispatch::Static looks at. Path is the raw, still percent-encoded
// path without the query string.
public sealed record StaticRequest(string Method, string Path)
{
    public string? AcceptEncoding { get; init; }
    public string? Range { get; init; }
    public string? IfModifiedSince { get; init; }
}

// Headers are in the order Rack builds them, spelled as Rack spells them ("Cache-Control" comes from
// the app's config). Body is empty for HEAD.
public sealed record StaticResponse(int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, ReadOnlyMemory<byte> Body)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

// ActionDispatch::Static (actionpack/lib/action_dispatch/middleware/static.rb) over Rack::Files
// (rack 3.2.6), serving an AssetBundle with the headers from reference/config/environments/
// production.rb. The last `config.public_file_server.headers` assignment there wins, so digested
// assets get the same 30-day public caching as everything else, not the immutable one-year policy
// the file sets first.
public sealed class StaticFiles(AssetBundle bundle)
{
    public const string CacheControl = "public, max-age=2592000";

    // Rack::Files::MULTIPART_BOUNDARY
    const string multipartBoundary = "AaB03x";

    readonly string lastModified = bundle.LastModified.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    // Answers a GET or HEAD for a static file; false hands the request on to the app.
    public async Task<bool> TryServeAsync(HttpContext context)
    {
        var request = context.Request;
        // Rack's PATH_INFO is the request target's path, still percent-encoded.
        var path = context.Features.Get<IHttpRequestFeature>()?.RawTarget is { } target && target.StartsWith('/')
            ? target.Split('?', 2)[0]
            : (request.PathBase + request.Path).ToString();
        var response = Serve(new StaticRequest(request.Method, path)
        {
            AcceptEncoding = request.Headers.AcceptEncoding.ToString(),
            Range = request.Headers.Range.Count > 0 ? request.Headers.Range.ToString() : null,
            IfModifiedSince = request.Headers.IfModifiedSince.Count > 0 ? request.Headers.IfModifiedSince.ToString() : null,
        });
        if (response is null)
        {
            return false;
        }

        context.Response.StatusCode = response.Status;
        foreach (var (name, value) in response.Headers)
        {
            context.Response.Headers[name] = value;
        }

        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        return true;
    }

    // FileHandler#attempt: a response when a file matches a GET or HEAD, null to fall through.
    public StaticResponse? Serve(StaticRequest request)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            return null;
        }

        return FindFile(request.Path, request.AcceptEncoding) is var (file, contentHeaders)
            ? Serving(request, file, contentHeaders)
            : null;
    }

    // FileHandler#find_file over each_candidate_filepath: the path, then (only for a path without a
    // known extension) path.html and path/index.html.
    (byte[] File, List<KeyValuePair<string, string>> ContentHeaders)? FindFile(string pathInfo, string? acceptEncoding)
    {
        if (CleanPath(pathInfo) is not { } path)
        {
            return null;
        }

        var extension = RubyPath.Extname(path);
        var contentType = RackMime.MimeType(extension, null);
        var candidates = new List<(string Path, string ContentType)> { (path, contentType ?? "text/plain") };
        if (contentType is null && extension != ".html")
        {
            candidates.Add((path + ".html", "text/html"));
            candidates.Add((path + "/index.html", "text/html"));
        }

        foreach (var (candidate, candidateType) in candidates)
        {
            if (TryFiles(candidate, candidateType, acceptEncoding ?? "") is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // FileHandler#try_files and #try_precompressed_files: br, then gzip, then the file itself.
    (byte[], List<KeyValuePair<string, string>>)? TryFiles(string path, string contentType, string acceptEncoding)
    {
        var headers = new List<KeyValuePair<string, string>> { new("content-type", contentType) };
        if (!Compressible(contentType))
        {
            return File(path) is { } plain ? (plain, headers) : null;
        }

        foreach (var (encoding, extension) in new[] { ("br", ".br"), ("gzip", ".gz") })
        {
            if (File(path + extension) is { } compressed)
            {
                Set(headers, "vary", "accept-encoding");
                if (Accepts(acceptEncoding, encoding))
                {
                    Set(headers, "content-encoding", encoding);
                    return (compressed, headers);
                }
            }
        }

        return File(path) is { } identity ? (identity, headers) : null;
    }

    // Rack::Files#serving, then FileHandler#serve's headers.update(content_headers) (skipped for 304).
    StaticResponse Serving(StaticRequest request, byte[] file, List<KeyValuePair<string, string>> contentHeaders)
    {
        if (request.IfModifiedSince == lastModified)
        {
            return new StaticResponse(304, [], ReadOnlyMemory<byte>.Empty);
        }

        var size = file.Length;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("last-modified", lastModified),
            new("content-type", contentHeaders[0].Value),
            new("Cache-Control", CacheControl),
        };
        var status = 200;
        ReadOnlyMemory<byte> body = file;

        switch (RackUtils.GetByteRanges(request.Range, size))
        {
            case null:
                break;
            case []:
                // Rack::Files#fail(416, ...) with the file's size; Static's content headers follow.
                const string Message = "Byte range unsatisfiable\n";
                headers =
                [
                    new("content-type", "text/plain"),
                    new("content-length", Message.Length.ToString(CultureInfo.InvariantCulture)),
                    new("x-cascade", "pass"),
                    new("content-range", $"bytes */{size}"),
                ];
                status = 416;
                body = Encoding.ASCII.GetBytes(Message);
                break;
            case [var (start, end)]:
                headers.Add(new("content-range", $"bytes {start}-{end}/{size}"));
                status = 206;
                body = file.AsMemory(start, end - start + 1);
                break;
            case var ranges:
                // Rack sets multipart/byteranges, which Static then overwrites with the file's type.
                var multipart = new MemoryStream();
                foreach (var (start, end) in ranges)
                {
                    multipart.Write(Encoding.ASCII.GetBytes(
                        $"\r\n--{multipartBoundary}\r\ncontent-type: {contentHeaders[0].Value}\r\ncontent-range: bytes {start}-{end}/{size}\r\n\r\n"));
                    multipart.Write(file, start, end - start + 1);
                }

                multipart.Write(Encoding.ASCII.GetBytes($"\r\n--{multipartBoundary}--\r\n"));
                status = 206;
                body = multipart.ToArray();
                break;
        }

        if (status != 416)
        {
            headers.Add(new("content-length", body.Length.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var (name, value) in contentHeaders)
        {
            Set(headers, name, value);
        }

        return new StaticResponse(status, headers, request.Method == "HEAD" ? ReadOnlyMemory<byte>.Empty : body);
    }

    byte[]? File(string path) => bundle.Files.GetValueOrDefault(path);

    static void Set(List<KeyValuePair<string, string>> headers, string name, string value)
    {
        var index = headers.FindIndex(header => header.Key == name);
        if (index >= 0)
        {
            headers[index] = new(name, value);
        }
        else
        {
            headers.Add(new(name, value));
        }
    }

    // FileHandler#clean_path: chomp("/"), Rack::Utils.unescape_path, valid_path? (no NUL), then
    // Rack::Utils.clean_path_info. Bytes that aren't UTF-8 can't name a file, so they don't match.
    static string? CleanPath(string pathInfo)
    {
        var bytes = UnescapePath(pathInfo.EndsWith('/') ? pathInfo[..^1] : pathInfo);
        if (bytes.Contains((byte)0))
        {
            return null;
        }

        string path;
        try
        {
            path = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var parts = path.Split('/');
        var clean = new List<string>();
        foreach (var part in parts)
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (clean.Count > 0)
                {
                    clean.RemoveAt(clean.Count - 1);
                }
            }
            else
            {
                clean.Add(part);
            }
        }

        var cleaned = string.Join('/', clean);
        return parts[0].Length == 0 ? "/" + cleaned : cleaned;
    }

    // URI::RFC2396_Parser#unescape: each %XX becomes a byte; anything else is kept.
    static byte[] UnescapePath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && IsHex(bytes[i + 1]) && IsHex(bytes[i + 2]))
            {
                output.Add(byte.Parse(Encoding.ASCII.GetString(bytes, i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                output.Add(bytes[i]);
            }
        }

        return [.. output];

        static bool IsHex(byte b) => char.IsAsciiHexDigit((char)b);
    }

    // FileHandler's compressible_content_types: /\A(?:text\/|application\/javascript|image\/svg\+xml)/
    static bool Compressible(string contentType) =>
        contentType.StartsWith("text/", StringComparison.Ordinal)
        || contentType.StartsWith("application/javascript", StringComparison.Ordinal)
        || contentType.StartsWith("image/svg+xml", StringComparison.Ordinal);

    // accept_encoding.any? { |enc, _| /\b#{encoding}\b/i.match?(enc) } over Rack's
    // parse_http_accept_header, which takes each comma-separated part up to its first ";".
    static bool Accepts(string acceptEncoding, string encoding)
    {
        foreach (var part in acceptEncoding.Split(','))
        {
            var attribute = part.Trim().Split(';', 2)[0].Trim();
            for (var at = attribute.IndexOf(encoding, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = attribute.IndexOf(encoding, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsWord(attribute, at - 1) && !IsWord(attribute, at + encoding.Length))
                {
                    return true;
                }
            }
        }

        return false;

        static bool IsWord(string text, int index) =>
            index >= 0 && index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_');
    }
}

static partial class RackUtils
{
    // Rack::Utils.get_byte_ranges(http_range, size, max_ranges: 100): null to ignore the header,
    // empty when it is unsatisfiable, otherwise inclusive (start, end) ranges.
    public static List<(int Start, int End)>? GetByteRanges(string? httpRange, int size)
    {
        if (size == 0 || httpRange is null)
        {
            return null;
        }

        if (BytesPattern().Match(httpRange) is not { Success: true } match)
        {
            return null;
        }

        var byteRange = match.Groups[1].Value;
        if (byteRange.Count(c => c == ',') >= 100)
        {
            return null;
        }

        var ranges = new List<(long Start, long End)>();
        foreach (var rangeSpec in SplitRangeSpecs(byteRange))
        {
            if (!rangeSpec.Contains('-', StringComparison.Ordinal))
            {
                return null;
            }

            var range = RubySplit(rangeSpec, '-');
            var r0 = range.Count > 0 ? range[0] : null;
            var r1 = range.Count > 1 ? range[1] : null;
            long start, end;
            if (string.IsNullOrEmpty(r0))
            {
                if (r1 is null)
                {
                    return null;
                }

                // A suffix range: the last r1 bytes.
                start = Math.Max(size - RubyToI(r1), 0);
                end = size - 1;
            }
            else
            {
                start = RubyToI(r0);
                if (r1 is null)
                {
                    end = size - 1;
                }
                else
                {
                    end = RubyToI(r1);
                    if (end < start)
                    {
                        return null;
                    }

                    end = Math.Min(end, size - 1);
                }
            }

            if (start <= end)
            {
                ranges.Add((start, end));
            }
        }

        if (ranges.Sum(range => range.End - range.Start + 1) > size)
        {
            return [];
        }

        return ranges.Select(range => ((int)range.Start, (int)range.End)).ToList();
    }

    // byte_range.split(/,[ \t]*/): trailing empty fields are dropped.
    static List<string> SplitRangeSpecs(string byteRange)
    {
        var specs = byteRange.Split(',').Select((part, i) => i == 0 ? part : part.TrimStart(' ', '\t')).ToList();
        while (specs.Count > 0 && specs[^1].Length == 0)
        {
            specs.RemoveAt(specs.Count - 1);
        }

        return specs;
    }

    // String#split with a single-character separator: trailing empty fields are dropped.
    static List<string> RubySplit(string text, char separator)
    {
        var fields = text.Split(separator).ToList();
        while (fields.Count > 0 && fields[^1].Length == 0)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }

    // String#to_i: leading whitespace, an optional sign, then digits (with single underscores
    // between them); anything else ends the number, and no digits is 0. Clamped to stay in range.
    static long RubyToI(string text)
    {
        var i = 0;
        while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
        {
            i++;
        }

        var negative = false;
        if (i < text.Length && text[i] is '+' or '-')
        {
            negative = text[i] == '-';
            i++;
        }

        long value = 0;
        for (; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                value = Math.Min(value * 10 + (text[i] - '0'), long.MaxValue / 20);
            }
            else if (text[i] != '_' || i + 1 >= text.Length || !char.IsAsciiDigit(text[i + 1]) || i == 0 || !char.IsAsciiDigit(text[i - 1]))
            {
                break;
            }
        }

        return negative ? -value : value;
    }

    [GeneratedRegex("bytes=([^;]+)")]
    private static partial Regex BytesPattern();
}
