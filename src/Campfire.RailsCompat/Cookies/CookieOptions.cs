namespace Campfire.RailsCompat.Cookies;

/// <summary>
/// Options for setting or deleting a cookie, matching Rails / Rack options.
/// </summary>
public sealed class CookieOptions
{
    public string? Value { get; set; }
    public string Path { get; set; } = "/";
    public string? Domain { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public bool Permanent { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public SameSiteMode? SameSite { get; set; } = SameSiteMode.Lax;
    public bool Partitioned { get; set; }

    public CookieOptions() { }

    public CookieOptions(string? value)
    {
        Value = value;
    }

    public CookieOptions Clone() => new()
    {
        Value = Value,
        Path = Path,
        Domain = Domain,
        Expires = Expires,
        Permanent = Permanent,
        Secure = Secure,
        HttpOnly = HttpOnly,
        SameSite = SameSite,
        Partitioned = Partitioned
    };
}
