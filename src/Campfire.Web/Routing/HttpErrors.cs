using Campfire.RailsCompat.Params;

namespace Campfire.Web.Routing;

/// <summary>
/// An exception that answers with a fixed status, like the Rails exceptions listed in
/// <c>ActionDispatch::ExceptionWrapper.rescue_responses</c>. Other layers' exceptions (record not
/// found, for one) implement it to get their Rails status from <see cref="ErrorPages"/>.
/// </summary>
public interface IHasHttpStatus
{
    int StatusCode { get; }
}

/// <summary><c>ActionController::RoutingError</c>: no route matches (404).</summary>
public sealed class RoutingErrorException : Exception, IHasHttpStatus
{
    public RoutingErrorException() : base("Routing Error")
    {
    }

    public RoutingErrorException(string message) : base(message)
    {
    }

    public RoutingErrorException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 404;
}

/// <summary><c>ActionController::BadRequest</c> (400).</summary>
public sealed class BadRequestException : Exception, IHasHttpStatus
{
    public BadRequestException() : base("Bad Request")
    {
    }

    public BadRequestException(string message) : base(message)
    {
    }

    public BadRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 400;
}

/// <summary><c>ActionController::UnknownHttpMethod</c>: a verb Rails doesn't know (405).</summary>
public sealed class UnknownHttpMethodException : Exception, IHasHttpStatus
{
    public UnknownHttpMethodException() : base("Unknown HTTP method")
    {
    }

    public UnknownHttpMethodException(string message) : base(message)
    {
    }

    public UnknownHttpMethodException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 405;
}

/// <summary>
/// <c>ActionController::NotImplemented</c> (501): what every route answers until its feature task
/// binds a handler.
/// </summary>
public sealed class NotImplementedRouteException : Exception, IHasHttpStatus
{
    public NotImplementedRouteException() : base("Not Implemented")
    {
    }

    public NotImplementedRouteException(string message) : base(message)
    {
    }

    public NotImplementedRouteException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 501;
}

/// <summary>
/// <c>ActionController::MissingController</c>: a route whose controller the reference doesn't
/// define (<c>rooms/settings#show</c>). Rails 8.2's dispatcher no longer turns the
/// <c>NameError</c> into a routing error, so it is a 500 (<c>routing/route_set.rb</c>).
/// </summary>
public sealed class MissingControllerException : Exception
{
    public MissingControllerException() : base("Missing controller")
    {
    }

    public MissingControllerException(string message) : base(message)
    {
    }

    public MissingControllerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The statuses <c>ExceptionWrapper.rescue_responses</c> gives the exceptions the port raises.</summary>
public static class HttpErrors
{
    public static int StatusFor(Exception exception) => exception switch
    {
        IHasHttpStatus known => known.StatusCode,
        ParamException param => param.StatusCode,
        ParameterMissingException => 400,
        RequestBodyTooLargeException => 413,
        _ => 500,
    };

    /// <summary>
    /// <c>ActionDispatch::Request::HTTP_METHODS</c>: the verbs <c>request.request_method</c>
    /// accepts; any other raises <c>ActionController::UnknownHttpMethod</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownMethods = new HashSet<string>(
    [
        "OPTIONS", "GET", "HEAD", "POST", "PUT", "DELETE", "TRACE", "CONNECT",
        "PROPFIND", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK",
        "VERSION-CONTROL", "REPORT", "CHECKOUT", "CHECKIN", "UNCHECKOUT", "MKWORKSPACE", "UPDATE", "LABEL", "MERGE", "BASELINE-CONTROL", "MKACTIVITY",
        "ORDERPATCH", "ACL", "SEARCH", "MKCALENDAR", "PATCH",
    ], StringComparer.Ordinal);
}
