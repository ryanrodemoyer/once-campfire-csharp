namespace Campfire.RailsCompat.Params;

/// <summary>Which Rails or Rack error a request's parameters raised.</summary>
public enum ParamErrorKind
{
    /// <summary><c>ActionDispatch::ParameterTypeError</c>, e.g. <c>a=1&amp;a[b]=2</c>.</summary>
    Type,

    /// <summary><c>ActionDispatch::InvalidParameterError</c>: bad %-encoding or invalid UTF-8.</summary>
    Invalid,

    /// <summary><c>ActionDispatch::ParamsTooDeepError</c>.</summary>
    TooDeep,

    /// <summary><c>Rack::QueryParser::QueryLimitError</c> and the multipart part limits.</summary>
    Limit,

    /// <summary>
    /// <c>ActionDispatch::Http::Parameters::ParseError</c> for a JSON body, or a malformed multipart
    /// body (<c>Rack::Multipart::Error</c>, <c>EmptyContentError</c>, <c>EOFError</c>).
    /// </summary>
    Parse,

    /// <summary><c>ActionDispatch::Http::MimeNegotiation::InvalidType</c>: an unparseable Content-Type.</summary>
    InvalidMimeType,
}

/// <summary>
/// A request whose parameters Rails refuses. Every kind is a 400 Bad Request in Rails except
/// <see cref="ParamErrorKind.InvalidMimeType"/>, which is a 406 Not Acceptable.
/// </summary>
public sealed class ParamException : Exception
{
    public ParamException() : this(ParamErrorKind.Invalid, "Invalid parameters")
    {
    }

    public ParamException(string message) : this(ParamErrorKind.Invalid, message)
    {
    }

    public ParamException(string message, Exception innerException) : base(message, innerException)
    {
        Kind = ParamErrorKind.Invalid;
    }

    public ParamException(ParamErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public ParamErrorKind Kind { get; }

    public int StatusCode => Kind == ParamErrorKind.InvalidMimeType ? 406 : 400;

    internal static ParamException TooDeep() => new(ParamErrorKind.TooDeep, "exceeded available parameter key space");

    internal static ParamException Parse() => new(ParamErrorKind.Parse, "Error occurred while parsing request parameters");
}

/// <summary><c>ActionController::ParameterMissing</c> (a 400 Bad Request).</summary>
public sealed class ParameterMissingException : Exception
{
    public ParameterMissingException() : this("")
    {
    }

    public ParameterMissingException(string key) : base($"param is missing or the value is empty or invalid: {key}")
    {
        Key = key;
    }

    public ParameterMissingException(string message, Exception innerException) : base(message, innerException)
    {
        Key = "";
    }

    public string Key { get; }
}

/// <summary>
/// The request body is bigger than the port buffers in memory. Rails reads any size; this is a 413
/// Payload Too Large.
/// </summary>
public sealed class RequestBodyTooLargeException : Exception
{
    public RequestBodyTooLargeException() : base("request body too large")
    {
    }

    public RequestBodyTooLargeException(string message) : base(message)
    {
    }

    public RequestBodyTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
