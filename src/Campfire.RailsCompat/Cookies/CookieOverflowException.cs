namespace Campfire.RailsCompat.Cookies;

/// <summary>
/// Thrown when a cookie's combined name and value size exceeds the 4096-byte limit,
/// matching Rails <c>ActionDispatch::Cookies::CookieOverflow</c>.
/// </summary>
public sealed class CookieOverflowException : Exception
{
    public CookieOverflowException(string message) : base(message) { }
}
