namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>ApplicationController</c> (reference/app/controllers/application_controller.rb): every app
/// controller's base, with its concerns as partial-class files beside this one.
/// <para>
/// <c>include AllowBrowser, Authentication, Authorization, BlockBannedRequests,
/// SetCurrentRequest, SetPlatform, TrackedRoomVisit, VersionHeaders</c> includes right to left, so
/// the concerns' <c>included</c> blocks run in reverse and the before callbacks are, in order:
/// <c>set_version_headers</c>, <c>Current.request = request</c>, <c>reject_banned_ip</c> (unless
/// GET or HEAD), <c>require_authentication</c>, <c>deny_bots</c>,
/// <c>verify_authenticity_token</c> (unless authenticated by bot key; Authentication's
/// <c>protect_from_forgery</c> moves it after <c>deny_bots</c>) and <c>allow_browser</c>. Then
/// <c>verify_same_origin_request</c> after the action.
/// </para>
/// </summary>
public partial class ApplicationController : Controller
{
    /// <summary>The chain every app controller starts from: <c>Callbacks.For&lt;MyController&gt;()</c>.</summary>
    public static ControllerCallbacks<ApplicationController> Callbacks { get; } = BaseCallbacks.For<ApplicationController>()
        .Before("set_version_headers", controller => controller.SetVersionHeaders())
        .Before(null, static _ => { })
        .Before("reject_banned_ip", controller => controller.RejectBannedIpAsync(), unless: controller => controller.IsSafeRequest)
        .Before("require_authentication", controller => controller.RequireAuthenticationAsync())
        .Before("deny_bots", controller => controller.DenyBots())
        .Before("verify_authenticity_token", controller => controller.VerifyAuthenticityToken(), unless: controller => controller.AuthenticatedBy == AuthenticatedBy.BotKey)
        .Before(null, controller => controller.AllowBrowserAsync());
}

/// <summary>
/// Class-level declarations from the concerns, for building a controller's chain the way its
/// class body reads (<c>allow_unauthenticated_access only: :new</c>).
/// </summary>
public static class ApplicationControllerCallbacks
{
    /// <summary><c>allow_unauthenticated_access</c>: <c>skip_before_action :require_authentication</c>.</summary>
    public static ControllerCallbacks<T> AllowUnauthenticatedAccess<T>(
        this ControllerCallbacks<T> callbacks,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null) where T : ApplicationController =>
        callbacks.SkipBefore("require_authentication", only, except);

    /// <summary><c>allow_bot_access</c>: <c>skip_before_action :deny_bots</c>.</summary>
    public static ControllerCallbacks<T> AllowBotAccess<T>(
        this ControllerCallbacks<T> callbacks,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null) where T : ApplicationController =>
        callbacks.SkipBefore("deny_bots", only, except);

    /// <summary>
    /// <c>require_unauthenticated_access</c>: skip <c>require_authentication</c>, then (at the end of
    /// the chain so far) <c>restore_authentication</c> and <c>redirect_signed_in_user_to_root</c>.
    /// </summary>
    public static ControllerCallbacks<T> RequireUnauthenticatedAccess<T>(
        this ControllerCallbacks<T> callbacks,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null) where T : ApplicationController =>
        callbacks.SkipBefore("require_authentication", only, except)
            .Before("restore_authentication", async controller => await controller.RestoreAuthenticationAsync().ConfigureAwait(false), only, except)
            .Before("redirect_signed_in_user_to_root", controller => controller.RedirectSignedInUserToRoot(), only, except);

    /// <summary><c>skip_forgery_protection</c>: <c>skip_before_action :verify_authenticity_token</c>.</summary>
    public static ControllerCallbacks<T> SkipForgeryProtection<T>(
        this ControllerCallbacks<T> callbacks,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null) where T : Controller =>
        callbacks.SkipBefore("verify_authenticity_token", only, except);

    /// <summary><c>before_action :ensure_can_administer</c> (Authorization).</summary>
    public static ControllerCallbacks<T> EnsureCanAdminister<T>(
        this ControllerCallbacks<T> callbacks,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null) where T : ApplicationController =>
        callbacks.Before("ensure_can_administer", controller => controller.EnsureCanAdminister(), only, except);
}
