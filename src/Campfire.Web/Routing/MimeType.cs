using System.Text.RegularExpressions;

namespace Campfire.Web.Routing;

/// <summary>
/// <c>Mime::Type</c> (actionpack <c>http/mime_type.rb</c>), with the types Rails registers
/// (<c>http/mime_types.rb</c>) plus turbo-rails' <c>turbo_stream</c>, in registration order.
/// A type parsed from a header that nobody registered has no <see cref="Symbol"/>.
/// </summary>
public sealed partial class MimeType
{
    static readonly List<MimeType> Registered = [];
    static readonly Dictionary<string, MimeType> ByString = new(StringComparer.Ordinal);
    static readonly Dictionary<string, MimeType> ByExtension = new(StringComparer.Ordinal);

    public static readonly MimeType Html = Register("text/html", "html", ["application/xhtml+xml"], ["xhtml"]);
    public static readonly MimeType Text = Register("text/plain", "text", [], ["txt"]);
    public static readonly MimeType Js = Register("text/javascript", "js", ["application/javascript", "application/x-javascript"]);
    public static readonly MimeType Css = Register("text/css", "css");
    public static readonly MimeType Ics = Register("text/calendar", "ics");
    public static readonly MimeType Csv = Register("text/csv", "csv");
    public static readonly MimeType Vcf = Register("text/vcard", "vcf");
    public static readonly MimeType Vtt = Register("text/vtt", "vtt", [], ["vtt"]);
    public static readonly MimeType Md = Register("text/markdown", "md", [], ["md", "markdown"]);
    public static readonly MimeType Png = Register("image/png", "png", [], ["png"]);
    public static readonly MimeType Jpeg = Register("image/jpeg", "jpeg", [], ["jpg", "jpeg", "jpe", "pjpeg"]);
    public static readonly MimeType Gif = Register("image/gif", "gif", [], ["gif"]);
    public static readonly MimeType Bmp = Register("image/bmp", "bmp", [], ["bmp"]);
    public static readonly MimeType Tiff = Register("image/tiff", "tiff", [], ["tif", "tiff"]);
    public static readonly MimeType Svg = Register("image/svg+xml", "svg");
    public static readonly MimeType Webp = Register("image/webp", "webp", [], ["webp"]);
    public static readonly MimeType Mpeg = Register("video/mpeg", "mpeg", [], ["mpg", "mpeg", "mpe"]);
    public static readonly MimeType Mp3 = Register("audio/mpeg", "mp3", [], ["mp1", "mp2", "mp3"]);
    public static readonly MimeType Ogg = Register("audio/ogg", "ogg", [], ["oga", "ogg", "spx", "opus"]);
    public static readonly MimeType M4a = Register("audio/aac", "m4a", ["audio/mp4"], ["m4a", "mpg4", "aac"]);
    public static readonly MimeType Webm = Register("video/webm", "webm", [], ["webm"]);
    public static readonly MimeType Mp4 = Register("video/mp4", "mp4", [], ["mp4", "m4v"]);
    public static readonly MimeType Otf = Register("font/otf", "otf", [], ["otf"]);
    public static readonly MimeType Ttf = Register("font/ttf", "ttf", [], ["ttf"]);
    public static readonly MimeType Woff = Register("font/woff", "woff", [], ["woff"]);
    public static readonly MimeType Woff2 = Register("font/woff2", "woff2", [], ["woff2"]);
    public static readonly MimeType Xml = Register("application/xml", "xml", ["text/xml", "application/x-xml"]);
    public static readonly MimeType Rss = Register("application/rss+xml", "rss");
    public static readonly MimeType Atom = Register("application/atom+xml", "atom");
    public static readonly MimeType Yaml = Register("application/x-yaml", "yaml", ["text/yaml"], ["yml", "yaml"]);
    public static readonly MimeType MultipartForm = Register("multipart/form-data", "multipart_form");
    public static readonly MimeType UrlEncodedForm = Register("application/x-www-form-urlencoded", "url_encoded_form");
    public static readonly MimeType Json = Register("application/json", "json", ["text/x-json", "application/jsonrequest", "application/problem+json"]);
    public static readonly MimeType Pdf = Register("application/pdf", "pdf", [], ["pdf"]);
    public static readonly MimeType Zip = Register("application/zip", "zip", [], ["zip"]);
    public static readonly MimeType Gzip = Register("application/gzip", "gzip", ["application/x-gzip"], ["gz"]);

    /// <summary>turbo-rails' engine: <c>Mime::Type.register "text/vnd.turbo-stream.html", :turbo_stream</c>.</summary>
    public static readonly MimeType TurboStream = Register("text/vnd.turbo-stream.html", "turbo_stream");

    /// <summary><c>Mime::ALL</c>. It isn't registered, so a parsed <c>*/*</c> is a symbol-less type equal to it.</summary>
    public static readonly MimeType All = new("*/*", null, []);

    readonly string[] synonyms;

    MimeType(string value, string? symbol, string[] synonyms)
    {
        Value = value;
        Symbol = symbol;
        this.synonyms = synonyms;
    }

    /// <summary>The type as Rails prints it, e.g. <c>text/html</c>.</summary>
    public string Value { get; }

    /// <summary>The format name, e.g. <c>html</c>; null for an unregistered type.</summary>
    public string? Symbol { get; }

    /// <summary><c>ref</c>: the symbol, or the string for an unregistered type.</summary>
    public string Ref => Symbol ?? Value;

    public bool IsAll => Value == "*/*";

    /// <summary>All registered types, in registration order (<c>Mime::SET</c>).</summary>
    public static IReadOnlyList<MimeType> RegisteredTypes => Registered;

    /// <summary><c>Mime[extension]</c>: the type registered for a format name or extension.</summary>
    public static MimeType? LookupByExtension(string? extension) =>
        extension is not null && ByExtension.TryGetValue(extension, out var type) ? type : null;

    /// <summary>
    /// <c>Mime::Type.lookup</c>: the registered type for a string (or one of its synonyms), else a new
    /// symbol-less type, which must be a valid MIME type (<see cref="InvalidMimeTypeException"/>).
    /// </summary>
    public static MimeType Lookup(string value)
    {
        if (ByString.TryGetValue(value, out var type))
        {
            return type;
        }
        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        var bare = (semicolon < 0 ? value : value[..semicolon]).TrimEnd(RubyWhitespace);
        if (ByString.TryGetValue(bare, out type))
        {
            return type;
        }
        if (!ValidMimeType().IsMatch(bare))
        {
            throw new InvalidMimeTypeException($"\"{bare}\" is not a valid MIME type");
        }
        return new MimeType(bare, null, []);
    }

    /// <summary>
    /// <c>Mime::Type.parse</c>: an Accept header as types in preference order (q-values, then
    /// position, <c>*/*</c> last by default, <c>text/*</c> and <c>application/*</c> expanded to the
    /// registered types they match, duplicates dropped).
    /// </summary>
    public static List<MimeType> Parse(string acceptHeader)
    {
        if (!acceptHeader.Contains(',', StringComparison.Ordinal))
        {
            var match = ParameterSeparator().Match(acceptHeader);
            if (match.Success)
            {
                acceptHeader = acceptHeader[..match.Index].Trim(RubyWhitespace);
            }
            if (string.IsNullOrWhiteSpace(acceptHeader))
            {
                return [];
            }
            return ParseTrailingStar(acceptHeader) ?? [Lookup(acceptHeader)];
        }

        var items = new List<AcceptItem>();
        var index = 0;
        foreach (Match header in AcceptHeader().Matches(acceptHeader))
        {
            var pieces = ParameterSeparator().Split(header.Value, 2);
            var parameters = pieces[0].Trim(RubyWhitespace);
            // String#split drops a trailing empty field, so "text/html;q=" has no q.
            var q = pieces.Length > 1 && pieces[1].Length > 0 ? pieces[1] : null;
            if (parameters.Length == 0)
            {
                continue;
            }
            foreach (var name in ParseTrailingStar(parameters)?.Select(type => type.Value) ?? [parameters])
            {
                items.Add(new AcceptItem(index++, name, q));
            }
        }
        return SortAcceptList(items);
    }

    public override string ToString() => Value;

    public override bool Equals(object? obj) =>
        obj is MimeType other && (ReferenceEquals(this, other) || Value == other.Value
            || synonyms.Contains(other.Value) || other.synonyms.Contains(Value)
            || (Symbol is not null && Symbol == other.Symbol));

    public override int GetHashCode() => Symbol?.GetHashCode(StringComparison.Ordinal) ?? Value.GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(MimeType? left, MimeType? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(MimeType? left, MimeType? right) => !(left == right);

    static MimeType Register(string value, string symbol, string[]? synonyms = null, string[]? extensions = null)
    {
        var type = new MimeType(value, symbol, synonyms ?? []);
        Registered.Add(type);
        foreach (var name in (string[])[value, .. type.synonyms])
        {
            ByString[name] = type;
        }
        foreach (var extension in (string[])[symbol, .. extensions ?? []])
        {
            ByExtension[extension] = type;
        }
        return type;
    }

    // parse_trailing_star: text/* and application/* mean every registered type whose string or a
    // synonym contains "text" (or "application"), Regexp.quote'd, so "text" also matches "context".
    static List<MimeType>? ParseTrailingStar(string accept)
    {
        var match = TrailingStar().Match(accept);
        if (!match.Success)
        {
            return null;
        }
        var family = match.Groups[1].Value;
        return Registered.Where(type => type.Value.Contains(family, StringComparison.Ordinal)
            || type.synonyms.Any(synonym => synonym.Contains(family, StringComparison.Ordinal))).ToList();
    }

    // AcceptList.sort!: by q descending then position, with Rails' text/xml and +xml shuffle.
    static List<MimeType> SortAcceptList(List<AcceptItem> items)
    {
        var list = items.OrderByDescending(item => item.Q).ThenBy(item => item.Index).ToList();
        var textXml = list.FindIndex(item => item.Name == "text/xml");
        var appXml = list.FindIndex(item => item.Name == Xml.Value);
        if (textXml >= 0 && appXml >= 0)
        {
            list[appXml].Q = Math.Max(list[textXml].Q, list[appXml].Q);
            if (appXml > textXml)
            {
                (list[appXml], list[textXml]) = (list[textXml], list[appXml]);
                (appXml, textXml) = (textXml, appXml);
            }
            list.RemoveAt(textXml);
        }
        else if (textXml >= 0)
        {
            list[textXml].Name = Xml.Value;
        }
        if (appXml >= 0)
        {
            var app = list[appXml];
            for (var i = appXml; i < list.Count; i++)
            {
                if (list[i].Q < app.Q)
                {
                    break;
                }
                if (list[i].Name.EndsWith("+xml", StringComparison.Ordinal))
                {
                    (list[appXml], list[i]) = (list[i], app);
                    appXml = i;
                }
            }
        }

        var types = new List<MimeType>();
        foreach (var item in list)
        {
            var type = Lookup(item.Name);
            if (!types.Contains(type))
            {
                types.Add(type);
            }
        }
        return types;
    }

    static readonly char[] RubyWhitespace = [' ', '\t', '\n', '\v', '\f', '\r', '\0'];

    [GeneratedRegex(@"^(text|application)/\*", RegexOptions.Multiline)]
    private static partial Regex TrailingStar();

    [GeneratedRegex(@";\s*q=""?")]
    private static partial Regex ParameterSeparator();

    [GeneratedRegex(@"[^,\s""](?:[^,""]|""[^""]*"")*")]
    private static partial Regex AcceptHeader();

    // MIME_REGEXP
    [GeneratedRegex(@"\A(?:\*/\*|[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}/(?:\*|[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126})(?>\s*;\s*[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}(?:=(?:[a-zA-Z0-9][a-zA-Z0-9!#$&\-^_.+]{0,126}|""[^""\r\\]*""))?)*\s*)\z")]
    private static partial Regex ValidMimeType();

    sealed class AcceptItem(int index, string name, string? q)
    {
        public int Index { get; } = index;

        public string Name { get; set; } = name;

        // ((q || 1.0).to_f * 100).to_i, with */* defaulting to 0.
        public int Q { get; set; } = (int)((q is null ? (name == "*/*" ? 0.0 : 1.0) : Campfire.RailsCompat.Ruby.RubyFloat.ToF(q)) * 100);
    }
}

/// <summary><c>ActionDispatch::Http::MimeNegotiation::InvalidType</c> (406).</summary>
public sealed class InvalidMimeTypeException : Exception, IHasHttpStatus
{
    public InvalidMimeTypeException() : base("Invalid MIME type")
    {
    }

    public InvalidMimeTypeException(string message) : base(message)
    {
    }

    public InvalidMimeTypeException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 406;
}
