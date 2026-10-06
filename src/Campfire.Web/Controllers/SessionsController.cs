using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>SessionsController</c> (reference/app/controllers/sessions_controller.rb): sign in and out.
/// </summary>
public sealed class SessionsController : ApplicationController
{
    /// <summary><c>rate_limit to: 10, within: 3.minutes, only: :create</c></summary>
    public static readonly RateLimit CreateRateLimit = new(10, TimeSpan.FromMinutes(3));

    static readonly ControllerCallbacks<SessionsController> Chain = Callbacks.For<SessionsController>()
        .AllowUnauthenticatedAccess(only: ["new", "create"])
        .Before(null, c => c.RateLimitAsync(), only: ["create"])
        .Before("ensure_user_exists", c => c.EnsureUserExistsAsync(), only: ["new"]);

    public static readonly RequestDelegate New = Action(Chain, c => c.RenderActionAsync(c.RenderNew));

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    async ValueTask CreateAsync()
    {
        if (await AuthenticateByAsync(Params["email_address"], Params["password"]).ConfigureAwait(false) is { } user)
        {
            await StartNewSessionForAsync(user).ConfigureAwait(false);
            RedirectTo(PostAuthenticatingUrl());
        }
        else
        {
            await RenderRejectionAsync(401).ConfigureAwait(false);
        }
    }

    async ValueTask DestroyAsync()
    {
        await RemovePushSubscriptionAsync().ConfigureAwait(false);
        await TerminateCurrentSessionAsync().ConfigureAwait(false);
        RedirectTo(Routes.RootUrl(RequestUrl.UrlBase));
    }

    // `rate_limiting`: the request counts against the remote IP's window, and past the limit is
    // answered with `render_rejection :too_many_requests`.
    async ValueTask RateLimitAsync()
    {
        if (CreateRateLimit.Increment(App, $"rate-limit:{ControllerPath}:{RemoteIp}", Now) > CreateRateLimit.To)
        {
            await RenderRejectionAsync(429).ConfigureAwait(false);
        }
    }

    // `redirect_to first_run_url if User.none?`
    async ValueTask EnsureUserExistsAsync()
    {
        if (await ReadAsync(session => Users.Count(session) == 0).ConfigureAwait(false))
        {
            RedirectTo(Routes.FirstRunUrl(RequestUrl.UrlBase));
        }
    }

    ValueTask RenderRejectionAsync(int status)
    {
        Flash.Now["alert"] = "Too many requests or unauthorized.";
        return this.RenderTemplateAsync(RenderNew, status);
    }

    void RenderNew(View view, Data.Sqlite.SqliteSession session, Campfire.Templates.HtmlWriter w)
    {
        // `Current.account.name`
        var account = Accounts.First(session) ?? throw new InvalidOperationException("undefined method 'name' for nil");
        view.SessionsNew(w, account.Name, Params["email_address"], SessionsPages.AdministratorContact(session));
    }

    // `User.active.authenticate_by(email_address:, password:)` (ActiveRecord::SecurePassword):
    // nil for a blank password; otherwise the active user with that address whose password it is.
    // With no such user it still digests the password, so a miss takes as long as a wrong password.
    async ValueTask<User?> AuthenticateByAsync(object? emailAddress, object? password)
    {
        if (password is null || IsEmpty(password))
        {
            return null;
        }
        var secret = RubyValues.ToS(password);
        var user = await ReadAsync(session => FindActiveByEmailAddress(session, emailAddress)).ConfigureAwait(false);
        if (user is null)
        {
            SecurePassword.Digest(secret);
            return null;
        }
        return SecurePassword.Authenticate(user.PasswordDigest, secret) ? user : null;
    }

    // `find_by(email_address:)`: nil matches no address here, as `IS NULL` would only find bots
    // and agents without passwords, which can't authenticate.
    static User? FindActiveByEmailAddress(Data.Sqlite.SqliteSession session, object? emailAddress) => emailAddress switch
    {
        string address => Users.FindActiveByEmailAddress(session, address),
        null => null,
        _ => throw new InvalidOperationException($"Unsupported value for email_address: {emailAddress.GetType().Name}"),
    };

    // `value.empty?` for a param.
    static bool IsEmpty(object value) => value switch
    {
        string text => text.Length == 0,
        ParamHash hash => hash.Count == 0,
        System.Collections.ICollection collection => collection.Count == 0,
        _ => false,
    };

    // `Push::Subscription.destroy_by(endpoint: params[:push_subscription_endpoint], user_id: Current.user.id)`
    async ValueTask RemovePushSubscriptionAsync()
    {
        if (Params["push_subscription_endpoint"] is not string endpoint || Current.User is not { } user)
        {
            return;
        }
        await WriteAsync(tx =>
        {
            tx.Session.Execute(
                """DELETE FROM "push_subscriptions" WHERE "push_subscriptions"."endpoint" = @endpoint AND "push_subscriptions"."user_id" = @user_id""",
                ("@endpoint", endpoint), ("@user_id", user.Id));
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>rate_limit to:, within:</c> over Rails' cache store: a counter per key whose window starts at
/// its first increment (Redis' <c>INCRBY</c> then <c>EXPIRE … NX</c>), kept per app.
/// </summary>
public sealed class RateLimit(int to, TimeSpan within)
{
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebApp, Dictionary<string, (int Count, DateTimeOffset ExpiresAt)>> stores = [];

    public int To { get; } = to;

    public TimeSpan Within { get; } = within;

    /// <summary><c>store.increment(key, 1, expires_in: within)</c>: the count after this request.</summary>
    public int Increment(WebApp app, string key, DateTimeOffset now)
    {
        var counters = stores.GetValue(app, _ => new(StringComparer.Ordinal));
        lock (counters)
        {
            var count = counters.TryGetValue(key, out var counter) && counter.ExpiresAt > now ? counter.Count + 1 : 1;
            counters[key] = (count, count == 1 ? now + Within : counter.ExpiresAt);
            return count;
        }
    }

    /// <summary>Forget every counter of <paramref name="app"/> (a cache clear).</summary>
    public void Clear(WebApp app)
    {
        if (stores.TryGetValue(app, out var counters))
        {
            lock (counters)
            {
                counters.Clear();
            }
        }
    }
}
