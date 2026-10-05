using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.RailsCompat.Params;

/// <summary>
/// <c>Rack::Multipart::Parser</c> (rack 3.2.6, <c>rack/multipart/parser.rb</c>) feeding a pair list,
/// as <c>Rack::Request#form_pairs</c> hands one to Rails. File parts stream to temp files as they
/// arrive; only part headers and text fields are held in memory, together capped at 16 MB.
/// </summary>
public sealed partial class MultipartParser
{
    /// <summary><c>Parser::BUFSIZE</c>: how much is read from the body at a time.</summary>
    public const int BufferSize = 1_048_576;

    /// <summary><c>BUFFERED_UPLOAD_BYTESIZE_LIMIT</c>: part headers and text fields together.</summary>
    public const int BufferedUploadByteSizeLimit = 16 * 1024 * 1024;

    /// <summary><c>PARSER_BYTESIZE_LIMIT</c>: the whole body, files included.</summary>
    public const long ParserByteSizeLimit = 10L * 1024 * 1024 * 1024;

    /// <summary><c>Rack::Utils.multipart_file_limit</c>.</summary>
    public const int FileLimit = 128;

    /// <summary><c>Rack::Utils.multipart_total_part_limit</c>.</summary>
    public const int TotalPartLimit = 4096;

    const int boundaryStartLimit = 16 * 1024;
    const int mimeHeaderByteSizeLimit = 64 * 1024;
    const int contentDispositionQuotedEscapesLimit = 8 * 1024;
    const int contentDispositionMaxParams = 16;
    const int contentDispositionMaxBytes = 1536;

    static readonly byte[] Eol = "\r\n"u8.ToArray();

    readonly byte[] dashBoundary;
    readonly int endBoundarySize;
    readonly int rxMaxSize;
    readonly string? tempDirectory;
    readonly List<Part> parts = [];

    // The StringScanner: data[0..length) with the scan position at pos.
    byte[] data = new byte[BufferSize];
    int length;
    int pos;
    long? totalBytesRead;

    State state = State.FastForward;
    int retainedSize;
    int openFiles;
    int quotedEscapes;

    MultipartParser(string boundary, string? tempDirectory, bool bounded)
    {
        dashBoundary = Encoding.Latin1.GetBytes("--" + boundary);
        endBoundarySize = boundary.Length + 4;
        rxMaxSize = boundary.Length + 6;
        this.tempDirectory = tempDirectory;
        totalBytesRead = bounded ? null : 0;
    }

    enum State
    {
        FastForward,
        ConsumeToken,
        MimeHead,
        MimeBody,
        Done,
    }

    enum Token
    {
        None,
        Boundary,
        EndBoundary,
    }

    /// <summary>
    /// <c>Parser.parse_boundary</c>: the boundary from a multipart Content-Type, or null when it has
    /// none (Rack then reads the body as a urlencoded form).
    /// </summary>
    public static string? ParseBoundary(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }
        var match = MultipartContentType().Match(contentType);
        if (!match.Success)
        {
            return null;
        }
        if (match.Groups[1].Length > 0)
        {
            throw MultipartError("whitespace between boundary parameter name and equal sign");
        }
        if (BoundaryParameter().IsMatch(contentType[(match.Index + match.Length)..]))
        {
            throw MultipartError("multiple boundary parameters found in multipart content type");
        }
        return match.Groups[2].Value;
    }

    /// <summary>
    /// <c>Rack::Multipart.parse_multipart</c> with a <c>ParamList</c>: the body's pairs in order, or
    /// null when the Content-Type has no boundary or the Content-Length is 0. File values are
    /// <see cref="UploadedFile"/>s, which the caller owns.
    /// </summary>
    public static async Task<List<ParamPair>?> ParseAsync(
        Stream body, long? contentLength, string? contentType, string? tempDirectory = null, CancellationToken cancellationToken = default)
    {
        if (contentLength == 0)
        {
            return null;
        }
        var boundary = ParseBoundary(contentType);
        if (boundary is null)
        {
            return null;
        }
        if (contentLength > ParserByteSizeLimit)
        {
            throw MultipartError($"multipart Content-Length {contentLength} exceeds limit of {ParserByteSizeLimit} bytes");
        }
        if (boundary.Length > 70)
        {
            // RFC 1521 Section 7.2.1 imposes a 70 character maximum for the boundary.
            throw MultipartError($"multipart boundary size too large ({boundary.Length} characters)");
        }

        var parser = new MultipartParser(boundary, tempDirectory, contentLength is not null);
        try
        {
            await parser.ParseAsync(new BoundedReader(body, contentLength), cancellationToken).ConfigureAwait(false);
            return parser.Result();
        }
        catch
        {
            await parser.DisposeFilesAsync().ConfigureAwait(false);
            throw;
        }
    }

    async Task ParseAsync(BoundedReader reader, CancellationToken cancellationToken)
    {
        await ReadDataAsync(reader, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var wantRead = state switch
            {
                State.FastForward => HandleFastForward(),
                State.ConsumeToken => HandleConsumeToken(),
                State.MimeHead => HandleMimeHead(),
                State.MimeBody => await HandleMimeBodyAsync(cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            if (wantRead is null)
            {
                return;
            }
            if (wantRead.Value)
            {
                await ReadDataAsync(reader, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    async Task ReadDataAsync(BoundedReader reader, CancellationToken cancellationToken)
    {
        // Fast-forwarding keeps everything read, since its limit counts it all.
        if (state != State.FastForward && pos > 0)
        {
            Compact();
        }
        if (data.Length - length < BufferSize)
        {
            Array.Resize(ref data, length + BufferSize);
        }
        var read = await reader.ReadAsync(data.AsMemory(length, BufferSize), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            // EmptyContentError: the body ended before the closing boundary.
            throw MultipartError("bad content body");
        }
        if (totalBytesRead is not null)
        {
            totalBytesRead += read;
            if (totalBytesRead > ParserByteSizeLimit)
            {
                throw MultipartError($"multipart upload exceeds limit of {ParserByteSizeLimit} bytes");
            }
        }
        length += read;
    }

    // Reads until the opening boundary. An end boundary first is invalid, but scanning goes on,
    // unless the body is nothing but that end boundary.
    bool? HandleFastForward()
    {
        while (true)
        {
            switch (ConsumeBoundary())
            {
                case Token.Boundary:
                    state = State.MimeHead;
                    return false;
                case Token.EndBoundary:
                    if (pos == endBoundarySize && Rest.SequenceEqual(Eol))
                    {
                        state = State.Done;
                        return false;
                    }
                    break;
                default:
                    // The actual limit is the higher of 16KB and the buffer size.
                    if (length > boundaryStartLimit)
                    {
                        throw MultipartError("multipart boundary not found within limit");
                    }
                    return true;
            }
        }
    }

    bool? HandleConsumeToken()
    {
        var token = ConsumeBoundary();
        // Break if we're at the end of a buffer, but not if it is the end of a field.
        state = token == Token.EndBoundary || (pos == length && token != Token.Boundary) ? State.Done : State.MimeHead;
        return false;
    }

    bool? HandleMimeHead()
    {
        var headEnd = Rest.IndexOf("\r\n\r\n"u8);
        if (headEnd < 0)
        {
            // The actual limit is the higher of 64KB and the buffer size.
            if (length - pos > mimeHeaderByteSizeLimit)
            {
                throw MultipartError("multipart mime part header too large");
            }
            return true;
        }

        var head = Encoding.Latin1.GetString(data, pos, headEnd + 2);
        pos += headEnd + 4;

        var contentType = HeaderValue(MultipartContentTypeHeader(), head) is { } type ? ObsUnfold().Replace(type, "$1") : null;
        string? name = null, filename = null, filenameStar = null;
        var disposition = HeaderValue(MultipartContentDispositionHeader(), head);
        if (disposition is not null && disposition.Length <= contentDispositionMaxBytes)
        {
            // OBS unfolding (RFC 5322 Section 2.2.3).
            (name, filename, filenameStar) = ParseDisposition(ObsUnfold().Replace(disposition, "$1"));
        }
        else
        {
            name = HeaderValue(MultipartContentIdHeader(), head);
        }

        var filenameEncoding = Encoding.UTF8;
        if (filenameStar is not null)
        {
            var fields = RubySplit(filenameStar, '\'', 3);
            if (fields.Length == 0)
            {
                // Encoding.find(nil) raises TypeError, which Rails answers with a 500.
                throw new InvalidOperationException("no implicit conversion of nil into String (filename*)");
            }
            filename = NormalizeFilename(fields.Length > 2 ? fields[2] : "");
            filenameEncoding = FindEncoding(fields[0]) ?? Encoding.UTF8;
        }
        else if (filename is not null)
        {
            filename = NormalizeFilename(filename);
        }

        if (string.IsNullOrEmpty(name))
        {
            name = filename ?? $"{contentType ?? "text/plain"}[]";
        }

        // Part heads are retained for the whole parse; text bodies are buffered in memory too.
        UpdateRetainedSize(head.Length);
        OnMimeHead(new Part(head, filename, filenameEncoding, contentType, name));
        state = State.MimeBody;
        return false;
    }

    async Task<bool?> HandleMimeBodyAsync(CancellationToken cancellationToken)
    {
        var part = parts[^1];
        if (FindBoundary(Rest, out var matchStart, out _, out _))
        {
            var body = data.AsMemory(pos, matchStart);
            await AppendAsync(part, body, cancellationToken).ConfigureAwait(false);
            pos += body.Length + 2; // skip \r\n after the content
            state = State.ConsumeToken;
            return false;
        }

        // Save what we have so far, keeping enough to recognize a boundary split across reads.
        if (rxMaxSize < length - pos)
        {
            var delta = length - pos - rxMaxSize;
            await AppendAsync(part, data.AsMemory(pos, delta), cancellationToken).ConfigureAwait(false);
            pos += delta;
            Compact();
        }
        return true;
    }

    async Task AppendAsync(Part part, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (part.File is { } file)
        {
            await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            UpdateRetainedSize(content.Length);
            part.Text!.Write(content.Span);
        }
    }

    void OnMimeHead(Part part)
    {
        if (part.Filename is not null)
        {
            part.Path = TempFiles.Create(tempDirectory, part.Filename);
            part.File = new FileStream(part.Path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            openFiles++;
        }
        else
        {
            part.Text = new MemoryStream();
        }
        parts.Add(part);

        if (openFiles >= FileLimit)
        {
            throw new ParamException(ParamErrorKind.Limit, "Maximum file multiparts in content reached");
        }
        if (parts.Count >= TotalPartLimit)
        {
            throw new ParamException(ParamErrorKind.Limit, "Maximum total multiparts in content reached");
        }
    }

    void UpdateRetainedSize(int size)
    {
        retainedSize += size;
        if (retainedSize > BufferedUploadByteSizeLimit)
        {
            throw MultipartError("multipart data over retained size limit");
        }
    }

    // Collector#each with MimePart#get_data, tag_multipart_encoding and the ParamList.
    List<ParamPair> Result()
    {
        var pairs = new List<ParamPair>(parts.Count);
        foreach (var part in parts)
        {
            if (part.File is { } file)
            {
                file.Dispose();
                var size = new FileInfo(part.Path!).Length;
                if (part.Filename == "")
                {
                    // A blank filename means no file was selected.
                    File.Delete(part.Path!);
                    continue;
                }
                var upload = new UploadedFile(
                    FilenameString(part.Filename!, part.FilenameEncoding), Utf8(part.ContentType), Utf8(part.Head)!, part.Path!, size);
                pairs.Add(new ParamPair(Encoding.Latin1.GetBytes(part.Name), upload, Encoding.UTF8));
            }
            else
            {
                var encoding = TextPartEncoding(part.ContentType);
                pairs.Add(new ParamPair(Encoding.Latin1.GetBytes(part.Name), part.Text!.ToArray(), encoding));
            }
        }
        return pairs;
    }

    async Task DisposeFilesAsync()
    {
        foreach (var part in parts)
        {
            if (part.File is { } file)
            {
                await file.DisposeAsync().ConfigureAwait(false);
                File.Delete(part.Path!);
            }
        }
    }

    ReadOnlySpan<byte> Rest => data.AsSpan(pos, length - pos);

    void Compact()
    {
        Buffer.BlockCopy(data, pos, data, 0, length - pos);
        length -= pos;
        pos = 0;
    }

    // consume_boundary: scan past the next boundary, or to the end of the buffer.
    Token ConsumeBoundary()
    {
        if (FindBoundary(Rest, out _, out var matchEnd, out var isEnd))
        {
            pos += matchEnd;
            return isEnd ? Token.EndBoundary : Token.Boundary;
        }
        pos = length;
        return Token.None;
    }

    // /(?:\r\n|\A)--#{boundary}(?:\r\n|--)/, leftmost match, with \A at the scan position.
    bool FindBoundary(ReadOnlySpan<byte> rest, out int matchStart, out int matchEnd, out bool isEnd)
    {
        for (var from = 0; from <= rest.Length;)
        {
            var at = rest[from..].IndexOf(dashBoundary);
            if (at < 0)
            {
                break;
            }
            at += from;
            var start = at == 0 ? 0 : at >= 2 && rest[at - 2] == '\r' && rest[at - 1] == '\n' ? at - 2 : -1;
            var after = at + dashBoundary.Length;
            if (start >= 0 && after + 2 <= rest.Length && (rest[after..(after + 2)].SequenceEqual(Eol) || rest[after..(after + 2)].SequenceEqual("--"u8)))
            {
                matchStart = start;
                matchEnd = after + 2;
                isEnd = rest[after] == '-';
                return true;
            }
            from = at + 1;
        }
        matchStart = matchEnd = 0;
        isEnd = false;
        return false;
    }

    // The quoted/unquoted parameter walk in handle_mime_head, on the header as bytes.
    (string? Name, string? Filename, string? FilenameStar) ParseDisposition(string disposition)
    {
        string? name = null, filename = null, filenameStar = null;

        // Ignore the actual content-disposition value (should always be form-data). Rack slices up
        // to index(';') + 1 without checking for nil, so a disposition without parameters raises
        // NoMethodError, which Rails answers with a 500.
        var semicolon = disposition.IndexOf(';');
        if (semicolon < 0)
        {
            throw new InvalidOperationException("undefined method '+' for nil (Rack::Multipart::Parser#handle_mime_head)");
        }
        var rest = disposition[(semicolon + 1)..];
        var numParams = 0;

        int i;
        while ((i = rest.IndexOf('=')) >= 0)
        {
            // Only parse up to max parameters, to avoid potential denial of service.
            if (++numParams > contentDispositionMaxParams)
            {
                break;
            }

            var param = RubyLstrip(rest[..i]);
            rest = rest[(i + 1)..];
            string value;

            if (rest.StartsWith('"'))
            {
                // Quoted: handle backslash escapes, keeping the backslash in an IE-style filename.
                rest = rest[1..];
                var quoted = new StringBuilder();
                while ((i = rest.IndexOfAny(['"', '\\'])) >= 0)
                {
                    var c = rest[i];
                    quoted.Append(rest, 0, i);
                    rest = rest[(i + 1)..];
                    if (c == '"')
                    {
                        break;
                    }
                    if (++quotedEscapes > contentDispositionQuotedEscapesLimit)
                    {
                        throw MultipartError("number of quoted escapes during content disposition parsing exceeds limit");
                    }
                    var escaped = rest.Length > 0 ? rest[..1] : "";
                    rest = rest.Length > 0 ? rest[1..] : rest;
                    if (param == "filename" && escaped != "\"")
                    {
                        quoted.Append(c).Append(escaped);
                    }
                    else
                    {
                        quoted.Append(escaped);
                    }
                }
                value = quoted.ToString();
            }
            else if ((i = rest.IndexOf(';')) >= 0)
            {
                // Unquoted (which may be invalid): the value ends at the semicolon.
                value = rest[..i];
                rest = rest[i..];
            }
            else
            {
                // No ending semicolon: the rest of the line is the value.
                value = RubyStrip(rest);
                rest = "";
            }

            switch (param)
            {
                case "name":
                    name = value;
                    break;
                case "filename":
                    filename = value;
                    break;
                case "filename*":
                    filenameStar = value;
                    break;
            }

            // Skip the trailing semicolon, to proceed to the next parameter.
            if ((i = rest.IndexOf(';')) >= 0)
            {
                rest = rest[(i + 1)..];
            }
        }
        return (name, filename, filenameStar);
    }

    // normalize_filename: unescape %XX when every % is a valid escape, then keep the basename,
    // since some browsers send full Windows paths.
    static string NormalizeFilename(string filename)
    {
        if (EscapeCandidates().Matches(filename).All(m => m.Length == 3 && char.IsAsciiHexDigit(m.Value[1]) && char.IsAsciiHexDigit(m.Value[2])))
        {
            filename = ValidEscape().Replace(filename, m => ((char)Convert.ToByte(m.Value[1..], 16)).ToString());
        }
        var segments = RubySplitDroppingTrailingEmpty(filename, ['/', '\\']);
        return segments.Length == 0 ? "" : segments[^1];
    }

    static string? HeaderValue(Regex header, string head)
    {
        var match = header.Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    // tag_multipart_encoding: a text/plain part's charset names its encoding. Rack strips fields
    // without checking for nil, so an empty Content-Type, or (for text/plain) an empty field or a
    // field without '=', raises NoMethodError, which Rails answers with a 500.
    static Encoding TextPartEncoding(string? contentType)
    {
        var encoding = Encoding.UTF8;
        if (contentType is null)
        {
            return encoding;
        }
        var fields = RubySplitDroppingTrailingEmpty(contentType, [';']);
        if (fields.Length == 0)
        {
            throw new InvalidOperationException("undefined method 'strip!' for nil (Rack::Multipart::Parser#tag_multipart_encoding)");
        }
        if (RubyStrip(fields[0]) != "text/plain")
        {
            return encoding;
        }
        foreach (var field in fields.Skip(1))
        {
            var kv = RubySplit(field, '=', 2);
            if (kv.Length < 2)
            {
                throw new InvalidOperationException("undefined method 'strip!' for nil (Rack::Multipart::Parser#tag_multipart_encoding)");
            }
            var charset = RubyStrip(kv[1]);
            if (charset.StartsWith('"') && charset.EndsWith('"'))
            {
                charset = charset.Length > 1 ? charset[1..^1] : "";
            }
            if (RubyStrip(kv[0]) == "charset")
            {
                encoding = FindEncoding(charset) ?? Encoding.Latin1;
            }
        }
        return encoding;
    }

    // Encoding.find, limited to the ASCII-compatible encodings .NET has built in. Anything else is
    // Ruby's BINARY for our purposes: every byte is valid, kept as one char.
    static Encoding? FindEncoding(string name)
    {
        try
        {
            var encoding = Encoding.GetEncoding(name);
            return encoding.CodePage is 65001 or 20127 or 28591 ? encoding : Encoding.Latin1;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // UploadedFile#initialize: encode the filename to UTF-8, or force it when that fails.
    static string FilenameString(string filename, Encoding encoding)
    {
        var bytes = Encoding.Latin1.GetBytes(filename);
        return encoding.CodePage == 65001 ? Encoding.UTF8.GetString(bytes) : encoding.GetString(bytes);
    }

    static string? Utf8(string? byteString) => byteString is null ? null : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(byteString));

    static ParamException MultipartError(string message) => new(ParamErrorKind.Parse, message);

    static readonly char[] RubyWhitespace = ['\0', '\t', '\n', '\v', '\f', '\r', ' '];

    static string RubyLstrip(string s) => s.TrimStart(RubyWhitespace);

    static string RubyStrip(string s) => s.Trim(RubyWhitespace);

    // String#split(sep, limit): empty input gives no fields; trailing empty fields are kept.
    static string[] RubySplit(string s, char separator, int limit) => s.Length == 0 ? [] : s.Split(separator, limit);

    // String#split(regexp) with no limit drops trailing empty fields.
    static string[] RubySplitDroppingTrailingEmpty(string s, char[] separators)
    {
        var fields = s.Split(separators);
        var count = fields.Length;
        while (count > 0 && fields[count - 1].Length == 0)
        {
            count--;
        }
        return fields[..count];
    }

    // MULTIPART: %r|\Amultipart/.*?boundary(\s*)=\"?([^\";,]+)\"?|ni
    [GeneratedRegex("\\Amultipart/.*?boundary([ \\t\\r\\n\\f\\v]*)=\"?([^\";,]+)\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartContentType();

    [GeneratedRegex("boundary[ \\t\\r\\n\\f\\v]*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryParameter();

    // FWS = /[ \t]+(?:\r\n[ \t]+)?/ and HEADER_VALUE = "(?:[^\r\n]|\r\n[ \t])*".
    [GeneratedRegex("^Content-Type:(?:[ \\t]+(?:\\r\\n[ \\t]+)?)?((?:[^\\r\\n]|\\r\\n[ \\t])*)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartContentTypeHeader();

    [GeneratedRegex("^Content-Disposition:(?:[ \\t]+(?:\\r\\n[ \\t]+)?)?((?:[^\\r\\n]|\\r\\n[ \\t])*)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartContentDispositionHeader();

    [GeneratedRegex("^Content-ID:(?:[ \\t]+(?:\\r\\n[ \\t]+)?)?((?:[^\\r\\n]|\\r\\n[ \\t])*)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartContentIdHeader();

    [GeneratedRegex("\\r\\n([ \\t])")]
    private static partial Regex ObsUnfold();

    // /%.?.?/: . doesn't match a newline.
    [GeneratedRegex("%[^\\n]?[^\\n]?")]
    private static partial Regex EscapeCandidates();

    [GeneratedRegex("%[0-9a-fA-F]{2}")]
    private static partial Regex ValidEscape();

    sealed class Part(string head, string? filename, Encoding filenameEncoding, string? contentType, string name)
    {
        public string Head { get; } = head;

        public string? Filename { get; } = filename;

        public Encoding FilenameEncoding { get; } = filenameEncoding;

        public string? ContentType { get; } = contentType;

        public string Name { get; } = name;

        public string? Path { get; set; }

        public FileStream? File { get; set; }

        public MemoryStream? Text { get; set; }
    }

    /// <summary>
    /// <c>BoundedIO</c>: reads at most Content-Length bytes, and a body that ends short of it is an
    /// error. Without a Content-Length it reads to the end. Each read fills the buffer, as
    /// <c>IO#read(size)</c> does.
    /// </summary>
    sealed class BoundedReader(Stream stream, long? contentLength)
    {
        long remaining = contentLength ?? long.MaxValue;

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (remaining == 0)
            {
                return 0;
            }
            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)remaining];
            }
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (contentLength is not null)
                    {
                        throw MultipartError("bad content body");
                    }
                    break;
                }
                total += read;
            }
            remaining -= total;
            return total;
        }
    }
}
