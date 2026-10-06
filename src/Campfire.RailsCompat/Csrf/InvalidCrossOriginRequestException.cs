namespace Campfire.RailsCompat.Csrf;

/// <summary>
/// <c>ActionController::InvalidCrossOriginRequest</c>, raised for a non-XHR JavaScript response.
/// The exceptions app answers 422.
/// </summary>
public sealed class InvalidCrossOriginRequestException : Exception
{
    public InvalidCrossOriginRequestException(string message) : base(message)
    {
    }
}
