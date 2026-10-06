using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Pipeline;

/// <summary>
/// What every controller shares: the database, the secret key base's key generator, the clock and
/// the configuration Rails reads at boot. <see cref="HandleAsync"/> is the app's request delegate:
/// the middleware Rails runs around the router (<c>Rack::Runtime</c>,
/// <c>ActionDispatch::RequestId</c>), then the <see cref="Router"/>.
/// </summary>
public sealed partial class WebApp
{
    public required SqliteDatabase Database { get; init; }

    /// <summary>The key generator over <c>secret_key_base</c> (cookies, signed ids, CSRF-free tokens).</summary>
    public required KeyGenerator Keys { get; init; }

    public required Router Router { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// <c>config.app_version</c>: <c>APP_VERSION</c>, else <c>GIT_REVISION</c>, else "0"
    /// (reference/config/initializers/version.rb).
    /// </summary>
    public string AppVersion { get; init; } = "0";

    /// <summary><c>config.git_revision</c>: <c>GIT_REVISION</c>, or null when it isn't set.</summary>
    public string? GitRevision { get; init; }

    /// <summary>
    /// <c>config.assume_ssl</c>: TLS ends at a proxy in front, so every request is HTTPS
    /// (reference/config/environments/production.rb: on unless <c>DISABLE_SSL</c>).
    /// </summary>
    public bool AssumeSsl { get; init; }

    /// <summary><c>config.action_dispatch.trusted_proxies</c> (Rails' defaults).</summary>
    public IReadOnlyList<IpNetwork> TrustedProxies { get; init; } = RemoteIp.TrustedProxies;

    /// <summary>
    /// <c>user.reset_remote_connections</c> (sign out): disconnects the user's Action Cable
    /// connections. The realtime lane provides it; until then sign out disconnects nothing.
    /// </summary>
    public Func<Campfire.Data.Records.User, Task>? ResetRemoteConnections { get; init; }

    public Microsoft.Extensions.Logging.ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>The session cookie store's options (<c>_campfire_session</c>, 20 years).</summary>
    public RailsCompat.Session.SessionConfig SessionConfig { get; init; } = new();

    /// <summary>
    /// <c>config/initializers/version.rb</c> from the environment: <c>APP_VERSION.presence ||
    /// GIT_REVISION.presence || "0"</c>, and <c>GIT_REVISION</c> as it is.
    /// </summary>
    public static (string AppVersion, string? GitRevision) VersionFrom(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var revision = environment("GIT_REVISION");
        var version = Presence(environment("APP_VERSION")) ?? Presence(revision) ?? "0";
        return (version, revision);
    }

    /// <summary>The app that is serving <paramref name="context"/>.</summary>
    public static WebApp Of(HttpContext context) =>
        context.Features.Get<WebApp>() ?? throw new InvalidOperationException("The request didn't come through WebApp.HandleAsync");

    /// <summary>
    /// The middleware Rails runs around the router, outermost first: <c>Rack::Deflater</c>
    /// (config.ru), <c>Rack::Runtime</c> (<c>X-Runtime</c>) and <c>ActionDispatch::RequestId</c>
    /// (<c>X-Request-Id</c>). The router's response is buffered so they can see it whole, error
    /// pages included; a controller that streams its body goes through <see cref="ResponseOutput"/>.
    /// </summary>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Set(this);
        var started = Stopwatch.GetTimestamp();
        var requestId = MakeRequestId(context.Request.Headers["X-Request-Id"].ToString());
        context.Features.Set(new RequestId(requestId));
        var output = new ResponseOutput(context.Response.Body, () =>
        {
            context.Response.Headers["X-Request-Id"] = requestId;
            context.Response.Headers["X-Runtime"] = Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture);
        });
        context.Features.Set(output);
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await Router.HandleAsync(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = output.Stream;
        }
        if (output.LowLevelError)
        {
            // An exception that escaped Rails' own error handling: Puma's bare 500.
            context.Response.Headers.Clear();
            context.Response.StatusCode = 500;
            context.Response.ContentLength = 0;
            return;
        }
        if (!output.Streamed)
        {
            output.AddHeaders();
            var rackLength = output.Buffered ? null : context.Response.ContentLength;
            await RackDeflater.SendAsync(context, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), rackLength, output.Stream).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>RequestId#make_request_id</c>: the client's <c>X-Request-Id</c> with anything but word
    /// characters, <c>-</c> and <c>@</c> removed, cut to 255 characters; else a new UUID.
    /// </summary>
    public static string MakeRequestId(string? header)
    {
        if (Presence(header) is { } given)
        {
            var cleaned = UnsafeRequestIdCharacters().Replace(given, "");
            return cleaned.Length > 255 ? cleaned[..255] : cleaned;
        }
        return Guid.NewGuid().ToString();
    }

    static string? Presence(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"[^A-Za-z0-9_\-@]")]
    private static partial Regex UnsafeRequestIdCharacters();
}

/// <summary>
/// Where the response really goes, which <see cref="WebApp.HandleAsync"/> installs as a feature:
/// the server's stream, for a controller that streams its body (<see cref="Controller.SendStream"/>)
/// past the buffer.
/// </summary>
public sealed class ResponseOutput(Stream stream, Action addHeaders)
{
    public Stream Stream { get; } = stream;

    /// <summary>The body went straight to <see cref="Stream"/>.</summary>
    public bool Streamed { get; set; }

    /// <summary>
    /// The body is a controller's, buffered: Rails sets no <c>Content-Length</c> on those, which the
    /// deflater looks at (error pages do set one).
    /// </summary>
    public bool Buffered { get; set; }

    /// <summary>An error that escaped Rails' exception handling (Puma answers a bare 500).</summary>
    public bool LowLevelError { get; set; }

    /// <summary>The outer middleware's headers (<c>X-Request-Id</c>, <c>X-Runtime</c>).</summary>
    public void AddHeaders() => addHeaders();
}

/// <summary><c>request.request_id</c>, which <see cref="WebApp.HandleAsync"/> installs as a feature.</summary>
public sealed record RequestId(string Value);
