using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Session;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Pipeline;

/// <summary>
/// One request's controller, in place of an <c>ActionController::Base</c> instance: the request
/// (<see cref="Request"/>, <see cref="RequestUrl"/>, <see cref="Params"/>, <see cref="RemoteIp"/>),
/// <see cref="Cookies"/>, <see cref="Session"/>, <see cref="Flash"/>, <see cref="Current"/>, and the
/// response it builds (status, headers, body, cache control), which is only sent once the action
/// and its callbacks are done, as Rack sends what the app returns.
/// <para>
/// A controller subclasses <see cref="ApplicationController"/> (or this class, for the framework's
/// own controllers), declares its callbacks once and binds each action with
/// <c>Action</c>; instance fields are its instance variables.
/// </para>
/// </summary>
public abstract partial class Controller
{
    HttpContext? httpContext;
    WebApp? app;
    RequestUrl? requestUrl;
    CookieJar? cookies;
    CookieSession? session;
    Flash? flash;
    Current? current;
    string? remoteIp;

    /// <summary><c>controller_path</c>: the route's controller, such as <c>rooms</c> or <c>accounts/bots</c>.</summary>
    public string ControllerPath { get; private set; } = "";

    /// <summary><c>action_name</c>: the route's action.</summary>
    public string ActionName { get; private set; } = "";

    public HttpContext HttpContext => httpContext ?? throw NotInitialized();

    public WebApp App => app ?? throw NotInitialized();

    /// <summary>The router's view of the request: method, path, <c>params</c>, formats.</summary>
    public RailsRequest Request { get; private set; } = null!;

    /// <summary><c>request.protocol</c>, <c>host</c>, <c>url</c> and the URL helpers' <see cref="UrlBase"/>.</summary>
    public RequestUrl RequestUrl => requestUrl ??= new RequestUrl(HttpContext.Request, App.AssumeSsl);

    /// <summary><c>params</c>: body, then query, then path parameters.</summary>
    public ParamHash Params => Request.Parameters;

    /// <summary>The time for this request, read once so every write and cookie agrees.</summary>
    public DateTimeOffset Now { get; private set; }

    /// <summary>
    /// Whether the controller includes <c>ActionController::Live</c> (as
    /// <c>ActiveStorage::Streaming</c> does), whose <c>Live::Response</c> is built without the
    /// default headers, defaults a written body's <c>Cache-Control</c> to <c>no-cache</c>, and
    /// writes the cookies set by commit once more than the cookie middleware does.
    /// </summary>
    protected virtual bool IsLive => false;

    /// <summary><c>Current</c></summary>
    public Current Current => current ??= new Current(this);

    /// <summary><c>cookies</c></summary>
    public CookieJar Cookies => cookies ??= CookieJar.FromHeaders(HttpContext.Request.Headers.Cookie, App.Keys, () => Now);

    /// <summary><c>session</c></summary>
    public CookieSession Session => session ??= new CookieSession(Cookies, App.SessionConfig);

    /// <summary><c>flash</c>, read from the session the first time it's used.</summary>
    public Flash Flash => flash ??= Flash.FromSessionValue(Session.GetNode("flash"));

    /// <summary><c>request.remote_ip</c>; throws <see cref="IpSpoofAttackException"/> as Rails raises.</summary>
    public string RemoteIp => remoteIp ??= Pipeline.RemoteIp.Calculate(HttpContext.Request, App.TrustedProxies);

    /// <summary><c>request.user_agent</c></summary>
    public string? UserAgent => Header("User-Agent");

    /// <summary><c>request.referer</c></summary>
    public string? Referer => Header("Referer");

    /// <summary><c>request.request_id</c> (<c>X-Request-Id</c>).</summary>
    public string? RequestId => HttpContext.Features.Get<RequestId>()?.Value;

    /// <summary>
    /// turbo-rails' <c>Turbo::RequestIdTracking</c>: the <c>X-Turbo-Request-Id</c> header, which
    /// broadcasts made during the request carry.
    /// </summary>
    public string? TurboRequestId => Header("X-Turbo-Request-Id");

    public CancellationToken RequestAborted => HttpContext.RequestAborted;

    /// <summary>A request header, or null.</summary>
    public string? Header(string name) =>
        HttpContext.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    /// <summary>A read on one of the database's readers.</summary>
    public Task<T> ReadAsync<T>(Func<SqliteSession, T> work) => App.Database.ReadAsync(work, RequestAborted);

    /// <summary>A write on the database's writer, in one transaction.</summary>
    public Task<T> WriteAsync<T>(Func<WriteTransaction, T> work) => App.Database.WriteAsync(work, RequestAborted);

    /// <summary>A write on the database's writer, in one transaction.</summary>
    public Task WriteAsync(Action<WriteTransaction> work) => App.Database.WriteAsync(work, RequestAborted);

    /// <summary>
    /// The request delegate for one action: a new <typeparamref name="TController"/> for the
    /// request, its <paramref name="callbacks"/> around <paramref name="action"/>, then the response
    /// (flash, session and cookies committed, cache headers, <c>Rack::ETag</c>,
    /// <c>Rack::ConditionalGet</c>, <c>Rack::Head</c>). Exceptions propagate to the router, which
    /// renders Rails' error page without the action's cookies, as <c>ShowExceptions</c> does.
    /// </summary>
    public static RequestDelegate Action<TController>(ControllerCallbacks<TController> callbacks, Func<TController, ValueTask> action)
        where TController : Controller, new()
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(action);
        return async context =>
        {
            var controller = new TController();
            controller.Initialize(context, WebApp.Of(context));
            try
            {
                await callbacks.ProcessAsync(controller, action).ConfigureAwait(false);
            }
            catch (IpSpoofAttackException) when (context.Features.Get<ResponseOutput>() is { } output)
            {
                // RemoteIp raises again while Rails renders the error, so it reaches Puma.
                output.LowLevelError = true;
                return;
            }
            await controller.SendResponseAsync().ConfigureAwait(false);
        };
    }

    /// <summary><c>Action</c> for a synchronous action.</summary>
    public static RequestDelegate Action<TController>(ControllerCallbacks<TController> callbacks, Action<TController> action)
        where TController : Controller, new()
    {
        ArgumentNullException.ThrowIfNull(action);
        return Action(callbacks, controller =>
        {
            action(controller);
            return ValueTask.CompletedTask;
        });
    }

    void Initialize(HttpContext context, WebApp webApp)
    {
        httpContext = context;
        app = webApp;
        Request = context.RailsRequest();
        Now = webApp.Clock.GetUtcNow();
        ControllerPath = Request.Route?.Controller ?? "";
        ActionName = Request.Route?.Action ?? "";
        StartResponse();
    }

    static InvalidOperationException NotInitialized() => new("The controller hasn't been given a request");
}
