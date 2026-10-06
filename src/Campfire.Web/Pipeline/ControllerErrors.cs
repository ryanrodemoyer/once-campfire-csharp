using Campfire.Web.Routing;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>ActiveRecord::RecordNotFound</c> (404): a <c>find</c> or <c>find_by!</c> with no row, such as
/// <c>RoomScoped#set_room</c> for a room the user isn't in.
/// </summary>
public sealed class RecordNotFoundException : Exception, IHasHttpStatus
{
    public RecordNotFoundException() : base("Record not found")
    {
    }

    public RecordNotFoundException(string message) : base(message)
    {
    }

    public RecordNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 404;
}

/// <summary>
/// <c>ActionController::InvalidAuthenticityToken</c> and <c>InvalidCrossOriginRequest</c>, which
/// <c>ExceptionWrapper.rescue_responses</c> answers with 422.
/// </summary>
public sealed class ForgeryProtectionException : Exception, IHasHttpStatus
{
    public ForgeryProtectionException() : base(RailsCompat.Csrf.ForgeryProtection.TokenFailureMessage)
    {
    }

    public ForgeryProtectionException(string message) : base(message)
    {
    }

    public ForgeryProtectionException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => RailsCompat.Csrf.ForgeryProtection.UnverifiedStatus;
}

/// <summary>
/// <c>ActionController::UnknownFormat</c> (406): <c>respond_to</c> offers no format the request
/// accepts.
/// </summary>
public sealed class UnknownFormatException : Exception, IHasHttpStatus
{
    public UnknownFormatException() : base("ActionController::UnknownFormat")
    {
    }

    public UnknownFormatException(string message) : base(message)
    {
    }

    public UnknownFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 406;
}

/// <summary>
/// <c>ActionController::MissingExactTemplate</c> (406): an action that rendered nothing, for an
/// interactive browser request (<c>default_render</c>).
/// </summary>
public sealed class MissingExactTemplateException : Exception, IHasHttpStatus
{
    public MissingExactTemplateException() : base("No template found")
    {
    }

    public MissingExactTemplateException(string message) : base(message)
    {
    }

    public MissingExactTemplateException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int StatusCode => 406;
}

/// <summary>
/// <c>ActionController::Redirecting::UnsafeRedirectError</c> (and its open-redirect and
/// path-relative subclasses): not in <c>rescue_responses</c>, so a 500.
/// </summary>
public sealed class UnsafeRedirectException : Exception
{
    public UnsafeRedirectException() : base("Unsafe redirect")
    {
    }

    public UnsafeRedirectException(string message) : base(message)
    {
    }

    public UnsafeRedirectException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary><c>AbstractController::DoubleRenderError</c>: rendering or redirecting twice (500).</summary>
public sealed class DoubleRenderException : Exception
{
    public DoubleRenderException() : base("Render and/or redirect were called multiple times in this action.")
    {
    }

    public DoubleRenderException(string message) : base(message)
    {
    }

    public DoubleRenderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// <c>ActionDispatch::RemoteIp::IpSpoofAttackError</c>: <c>Client-Ip</c> and the forwarded
/// addresses disagree. Not in <c>rescue_responses</c>, so a 500.
/// </summary>
public sealed class IpSpoofAttackException : Exception
{
    public IpSpoofAttackException() : base("IP spoofing attack?!")
    {
    }

    public IpSpoofAttackException(string message) : base(message)
    {
    }

    public IpSpoofAttackException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
