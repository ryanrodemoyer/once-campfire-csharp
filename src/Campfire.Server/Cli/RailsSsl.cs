using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;

namespace Campfire.Server.Cli;

/// <summary>
/// <c>config.assume_ssl</c> and <c>config.force_ssl</c>: ActionDispatch::AssumeSSL followed by
/// ActionDispatch::SSL with its default options (actionpack/lib/action_dispatch/middleware/
/// assume_ssl.rb and ssl.rb at the pinned revision). Both sit in front of the static files and the
/// router. The reference sets both from <c>DISABLE_SSL</c>, so every request reaching SSL is already
/// <c>ssl?</c>, its redirect to https never happens, and what remains is the HSTS header and
/// secure cookies.
/// </summary>
public static partial class RailsSsl
{
    // build_hsts_header(default_hsts_options): two years, includeSubDomains, no preload.
    public const string HstsHeader = "max-age=63072000; includeSubDomains";

    public static void Apply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AssumeSsl(context.Request);
        context.Response.OnStarting(() =>
        {
            FlagResponse(context.Response.Headers);
            return Task.CompletedTask;
        });
    }

    // AssumeSSL#call
    static void AssumeSsl(HttpRequest request)
    {
        request.Scheme = "https";
        request.Headers["X-Forwarded-Port"] = "443";
        request.Headers["X-Forwarded-Proto"] = "https";
    }

    // SSL#set_hsts_header! and #flag_cookies_as_secure! (Rack 3: set-cookie is an array).
    public static void FlagResponse(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!headers.ContainsKey("strict-transport-security"))
        {
            headers["strict-transport-security"] = HstsHeader;
        }

        if (headers.SetCookie is { Count: > 0 } cookies)
        {
            headers.SetCookie = new StringValues([.. cookies.Select(cookie => Secure().IsMatch(cookie!) ? cookie : $"{cookie}; secure")]);
        }
    }

    [GeneratedRegex(@";\s*secure\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Secure();
}
