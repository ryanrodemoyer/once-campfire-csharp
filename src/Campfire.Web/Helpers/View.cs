using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Session;
using Campfire.Web.Assets;
using Campfire.Web.Routing;

namespace Campfire.Web.Helpers;

/// <summary>
/// The view context one request renders with, the counterpart of the <c>ActionView::Base</c>
/// instance Rails builds per request: the helpers are its methods and the state they read
/// (assets, CSRF, the request, <c>Current</c>, <c>flash</c>, <c>content_for</c>) lives here.
/// Templates are partial instance methods of this class, declared in their own files, so a page,
/// its partials and the layout share one <c>content_for</c> store, as in Rails.
/// </summary>
public partial class View
{
    /// <summary>The asset manifest <c>asset_path</c> and <c>stylesheet_link_tag</c> resolve against.</summary>
    public required AssetBundle Assets { get; init; }

    /// <summary>
    /// The request's protocol, host and port (<c>request.base_url</c>), for <c>_url</c> helpers and
    /// <c>image_url</c>.
    /// </summary>
    public required UrlBase Origin { get; init; }

    /// <summary><c>request.path</c>, which per-form CSRF tokens resolve relative actions against.</summary>
    public string RequestPath { get; init; } = "/";

    /// <summary><c>request.url</c>.</summary>
    public string? RequestUrl { get; init; }

    /// <summary><c>request.referrer</c>.</summary>
    public string? Referrer { get; init; }

    /// <summary>
    /// <c>url_for({})</c>: the path of the current controller and action, which a <c>form_with</c>
    /// without a URL posts to. Defaults to <see cref="RequestPath"/>.
    /// </summary>
    public string? CurrentPath { get; init; }

    /// <summary>
    /// <c>form_authenticity_token(form_options: { action:, method: })</c>: the masked token for a
    /// form with that action and method, or the global token when both are null. Null when
    /// <c>protect_against_forgery?</c> is false, which leaves out the meta tags and hidden fields.
    /// Use <see cref="CsrfTokens"/> to build it from the session's token.
    /// </summary>
    public Func<string?, string?, string>? FormAuthenticityToken { get; init; }

    /// <summary>The key generator <c>turbo_stream_from</c> signs stream names with.</summary>
    public KeyGenerator? StreamKeys { get; init; }

    /// <summary>The request's flash, read by the layout.</summary>
    public Flash Flash { get; init; } = Flash.FromSessionValue(null);

    /// <summary><c>Current.user</c>, or null when signed out.</summary>
    public CurrentUser? CurrentUser { get; init; }

    /// <summary><c>Current.account</c>, or null before the first run.</summary>
    public CurrentAccount? CurrentAccount { get; init; }

    /// <summary><c>Rails.configuration.x.vapid.public_key</c> (<c>VAPID_PUBLIC_KEY</c>).</summary>
    public string? VapidPublicKey { get; init; }

    /// <summary>
    /// <c>Rails.application.config.app_version</c>: <c>APP_VERSION</c>, else <c>GIT_REVISION</c>,
    /// else "0" (reference/config/initializers/version.rb).
    /// </summary>
    public string AppVersion { get; init; } = "0";

    /// <summary><c>@page_title</c>.</summary>
    public string? PageTitle { get; set; }

    /// <summary><c>@body_class</c>.</summary>
    public string? BodyClass { get; set; }
}

/// <summary>What the helpers read from <c>Current.user</c>.</summary>
public sealed record CurrentUser(long Id, string Name, bool CanAdminister);

/// <summary>What the helpers read from <c>Current.account</c>.</summary>
public sealed record CurrentAccount(string? CustomStyles, bool LogoAttached, DateTimeOffset? UpdatedAt);
