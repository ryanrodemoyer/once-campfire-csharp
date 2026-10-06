namespace Campfire.RailsCompat.Csrf;

/// <summary>
/// <c>ActionController::InvalidAuthenticityToken</c>. The exceptions app answers 422.
/// </summary>
public sealed class InvalidAuthenticityTokenException : Exception
{
    public InvalidAuthenticityTokenException(string message) : base(message)
    {
    }
}
