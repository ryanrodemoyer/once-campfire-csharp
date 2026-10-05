using Campfire.RailsCompat.Cookies;

namespace Campfire.RailsCompat.Session;

/// <summary>
/// Configuration for the session cookie store matching <c>reference/config/initializers/session_store.rb</c>.
/// </summary>
public sealed class SessionConfig
{
    public const string DefaultSessionKey = "_campfire_session";
    public const int DefaultExpireAfterYears = 20;

    public string Key { get; set; } = DefaultSessionKey;
    public int ExpireAfterYears { get; set; } = DefaultExpireAfterYears;
    public bool HttpOnly { get; set; } = true;
    public SameSiteMode? SameSite { get; set; } = SameSiteMode.Lax;
}
