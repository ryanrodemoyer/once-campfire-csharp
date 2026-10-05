namespace Campfire.RailsCompat.UserAgent;

/// <summary>
/// The useragent gem, or code reading it, raised where Ruby would (a <c>NoMethodError</c> on nil,
/// an <c>ArgumentError</c> comparing a String with an Integer). Rails turns these into a 500.
/// </summary>
public sealed class GemRaisedException : Exception
{
    public GemRaisedException()
    {
    }

    public GemRaisedException(string message) : base(message)
    {
    }

    public GemRaisedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
