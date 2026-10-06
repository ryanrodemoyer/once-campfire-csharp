using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Ruby;
using Microsoft.Extensions.Logging;

namespace Campfire.Web.Pipeline;

/// <summary><c>authenticated_by</c>: how <c>Current.user</c> got there (<c>"".inquiry</c> when nothing did).</summary>
public enum AuthenticatedBy
{
    None,
    Session,
    BotKey,
}

// reference/app/controllers/concerns/authentication.rb
public partial class ApplicationController
{
    /// <summary><c>authenticated_by</c></summary>
    public AuthenticatedBy AuthenticatedBy { get; private set; }

    /// <summary><c>signed_in?</c> (a helper method): there's a <c>Current.user</c>.</summary>
    public bool SignedIn => Current.User is not null;

    /// <summary>
    /// <c>require_authentication</c>: the session cookie's session, else a bot key, else off to
    /// sign in.
    /// </summary>
    public async ValueTask RequireAuthenticationAsync()
    {
        if (await RestoreAuthenticationAsync().ConfigureAwait(false) || await BotAuthenticationAsync().ConfigureAwait(false))
        {
            return;
        }
        RequestAuthentication();
    }

    /// <summary><c>restore_authentication</c>: resume the session the <c>session_token</c> cookie names.</summary>
    public async ValueTask<bool> RestoreAuthenticationAsync()
    {
        if (await FindSessionByCookieAsync().ConfigureAwait(false) is { } session)
        {
            await ResumeSessionAsync(session).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// <c>bot_authentication</c>: <c>params[:bot_key].present?</c> and an active bot with that key.
    /// A non-string <c>bot_key</c> fails as Rails' <c>strip</c> does (500).
    /// </summary>
    public async ValueTask<bool> BotAuthenticationAsync()
    {
        var param = Params["bot_key"];
        if (!IsPresent(param))
        {
            return false;
        }
        if (param is not string key)
        {
            throw new InvalidOperationException($"undefined method 'strip' for an instance of {param!.GetType().Name}");
        }
        var botKey = RubyString.Strip(key);
        var bot = await ReadAsync(session => Users.AuthenticateBot(session, botKey)).ConfigureAwait(false);
        if (bot is null)
        {
            return false;
        }
        Current.User = bot;
        AuthenticatedBy = AuthenticatedBy.BotKey;
        return true;
    }

    /// <summary>
    /// <c>request_authentication</c>: remember the URL in the session
    /// (<c>return_to_after_authenticating</c>) and redirect to sign in.
    /// </summary>
    public void RequestAuthentication()
    {
        Session["return_to_after_authenticating"] = RequestUrl.Url;
        RedirectTo(Routes.NewSessionUrl(RequestUrl.UrlBase));
    }

    /// <summary><c>redirect_signed_in_user_to_root</c></summary>
    public void RedirectSignedInUserToRoot()
    {
        if (SignedIn)
        {
            RedirectTo(Routes.RootUrl(RequestUrl.UrlBase));
        }
    }

    /// <summary><c>start_new_session_for(user)</c>: a new session row, signed in as it.</summary>
    public async ValueTask<Session> StartNewSessionForAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var (userAgent, ipAddress, now) = (UserAgent, RemoteIp, Now);
        var session = await WriteAsync(tx => Sessions.Start(tx.Session, user.Id, userAgent, ipAddress, now)).ConfigureAwait(false);
        await AuthenticatedAsAsync(session).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// <c>resume_session(session)</c>: <c>session.resume</c> (which records the browser at most once an
    /// hour), then signed in as it, which re-sends the <c>session_token</c> cookie.
    /// </summary>
    public async ValueTask ResumeSessionAsync(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var (userAgent, ipAddress, now) = (UserAgent, RemoteIp, Now);
        if (session.NeedsActivityRefresh(now))
        {
            session = await WriteAsync(tx => Sessions.Resume(tx.Session, session, userAgent, ipAddress, now)).ConfigureAwait(false);
        }
        await AuthenticatedAsAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>terminate_current_session</c>: destroy the session row, <c>reset_session</c>, drop the
    /// <c>session_token</c> cookie and disconnect the user's sockets (errors only logged).
    /// </summary>
    public async ValueTask TerminateCurrentSessionAsync()
    {
        if (Current.Session is { } session)
        {
            await WriteAsync(tx => Sessions.Delete(tx.Session, session.Id)).ConfigureAwait(false);
        }
        ResetSession();
        Cookies.RemoveAuthenticationCookie();
        await DisconnectRemoteConnectionsAsync().ConfigureAwait(false);
    }

    /// <summary><c>post_authenticating_url</c>: the remembered URL (taken out of the session), else the root.</summary>
    public string PostAuthenticatingUrl()
    {
        var stored = Session.Remove("return_to_after_authenticating");
        return stored switch
        {
            null => Routes.RootUrl(RequestUrl.UrlBase),
            System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var url) => url,
            var other => other.ToJsonString(),
        };
    }

    /// <summary><c>deny_bots</c>: 403 for a request a bot key authenticated.</summary>
    public void DenyBots()
    {
        if (AuthenticatedBy == AuthenticatedBy.BotKey)
        {
            Head(403);
        }
    }

    // authenticated_as(session): Current.session (and so Current.user), and a fresh cookie.
    async ValueTask AuthenticatedAsAsync(Session session)
    {
        var user = await ReadAsync(reader => Users.Find(reader, session.UserId)).ConfigureAwait(false);
        Current.SetSession(session, user);
        AuthenticatedBy = AuthenticatedBy.Session;
        Cookies.SetAuthenticationCookie(session.Token);
    }

    // disconnect_remote_connections: Current.user&.reset_remote_connections, failures logged.
    async ValueTask DisconnectRemoteConnectionsAsync()
    {
        if (Current.User is not { } user || App.ResetRemoteConnections is not { } reset)
        {
            return;
        }
        try
        {
            await reset(user).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogDisconnectFailed(App.Logger, error.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not disconnect remote connections on sign out: {Error}")]
    static partial void LogDisconnectFailed(ILogger logger, string error);

    // Object#present? for a param value.
    static bool IsPresent(object? value) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        RailsCompat.Params.ParamHash hash => hash.Count > 0,
        System.Collections.ICollection collection => collection.Count > 0,
        false => false,
        _ => true,
    };
}
