using System.Net;
using Campfire.Jobs.RestrictedHttp;
using Campfire.RichText.Sanitize;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// The endpoint rules of <c>Push::Subscription</c> (reference/app/models/push/subscription.rb):
/// only https on port 443 to a known push service, resolved through the private network guard.
/// Checked when a subscription is saved (<see cref="ValidationErrorsAsync"/>) and again on the delivery
/// worker before each push (<see cref="ResolveAsync"/>).
/// </summary>
public static class PushEndpoint
{
    /// <summary><c>PERMITTED_ENDPOINT_HOSTS</c></summary>
    public static readonly IReadOnlyList<string> PermittedHosts =
    [
        "jmt17.google.com",
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com",
        "notify.windows.com",
    ];

    /// <summary>
    /// <c>resolved_endpoint_ip</c>: the public address to pin, or null when the endpoint isn't a
    /// permitted push service or resolves to nothing public. It resolves on every call ("validate
    /// at point of use"), so the address can't have been rebound since.
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(PrivateNetworkGuard guard, string? endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (Parse(endpoint) is not { } uri || !IsPermitted(uri))
        {
            return null;
        }
        try
        {
            return await guard.ResolveAsync(uri.Host!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is PrivateNetworkViolationException or UnresolvableHostException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>validate :validate_endpoint_url</c> with <c>validates :endpoint, presence: true</c>: the
    /// messages added to <c>errors[:endpoint]</c>, none for a valid endpoint.
    /// </summary>
    public static async Task<List<string>> ValidationErrorsAsync(PrivateNetworkGuard guard, string? endpoint, CancellationToken cancellationToken = default)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errors.Add("can't be blank");
        }
        if (await ValidationErrorAsync(guard, endpoint, cancellationToken).ConfigureAwait(false) is { } error)
        {
            errors.Add(error);
        }
        return errors;
    }

    /// <summary><c>validate_endpoint_url</c>: its one message, or null.</summary>
    public static async Task<string?> ValidationErrorAsync(PrivateNetworkGuard guard, string? endpoint, CancellationToken cancellationToken = default)
    {
        var uri = Parse(endpoint);
        if (uri is null)
        {
            return "is not a valid URL";
        }
        if (uri.Scheme != "https")
        {
            return "must use HTTPS";
        }
        if (uri.Port != 443)
        {
            return "must use the default HTTPS port";
        }
        if (!IsPermittedHost(uri))
        {
            return "is not a permitted push service";
        }
        return await ResolveAsync(guard, endpoint, cancellationToken).ConfigureAwait(false) is null
            ? "resolves to a private or invalid IP address"
            : null;
    }

    /// <summary><c>endpoint_uri</c>: <c>URI.parse(endpoint) if endpoint.present?</c>, nil where it raises.</summary>
    public static RubyUri? Parse(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }
        try
        {
            return RubyUri.Parse(endpoint);
        }
        catch (RubyUriException)
        {
            return null;
        }
    }

    // `permitted_endpoint_uri?`
    static bool IsPermitted(RubyUri uri) => uri.Scheme == "https" && uri.Port == 443 && IsPermittedHost(uri);

    // `permitted_endpoint_host?`: a permitted host or a subdomain of one, ignoring case.
    static bool IsPermittedHost(RubyUri uri)
    {
        var host = uri.Host?.ToLowerInvariant();
        return !string.IsNullOrEmpty(host) &&
            PermittedHosts.Any(permitted => host == permitted || host.EndsWith($".{permitted}", StringComparison.Ordinal));
    }
}
