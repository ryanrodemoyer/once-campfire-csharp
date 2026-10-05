namespace Campfire.Jobs.RestrictedHttp;

/// <summary>
/// <c>RestrictedHTTP::Violation</c>: the host resolves only to blocked addresses, or isn't a host at
/// all. Never thrown for a lookup that failed; that's <see cref="UnresolvableHostException"/>.
/// </summary>
public sealed class PrivateNetworkViolationException : Exception
{
    public PrivateNetworkViolationException() : base("Attempt to access private IP")
    {
    }

    public PrivateNetworkViolationException(string message) : base(message)
    {
    }

    public PrivateNetworkViolationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public static PrivateNetworkViolationException For(string hostname) => new($"Attempt to access private IP via {hostname}");
}

/// <summary><c>Surfguard::Unresolvable</c>: NXDOMAIN, a failed lookup, or an empty or oversized answer.</summary>
public sealed class UnresolvableHostException : Exception
{
    public UnresolvableHostException() : base("Host could not be resolved")
    {
    }

    public UnresolvableHostException(string message) : base(message)
    {
    }

    public UnresolvableHostException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
