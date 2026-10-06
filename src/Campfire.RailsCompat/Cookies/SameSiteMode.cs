namespace Campfire.RailsCompat.Cookies;

/// <summary>
/// SameSite cookie attribute matching Rails / Rack options (:lax, :strict, :none).
/// </summary>
public enum SameSiteMode
{
    Lax,
    Strict,
    None
}
