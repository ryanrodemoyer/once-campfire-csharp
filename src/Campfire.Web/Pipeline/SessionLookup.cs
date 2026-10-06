using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Web.Pipeline;

// reference/app/controllers/concerns/authentication/session_lookup.rb, and reset_session, which
// every controller has (Active Storage's included).
public abstract partial class Controller
{
    /// <summary>
    /// <c>Authentication::SessionLookup#find_session_by_cookie</c>: the session whose token the
    /// signed <c>session_token</c> cookie holds.
    /// </summary>
    public async ValueTask<Session?> FindSessionByCookieAsync()
    {
        if (Cookies.Signed["session_token"] is not { } token)
        {
            return null;
        }
        return await ReadAsync(session => Sessions.FindByToken(session, token)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>reset_session</c>: a new, empty session (the next response sends its new id), no CSRF
    /// token and no flash.
    /// </summary>
    public void ResetSession()
    {
        Session.Reset();
        ResetCsrfToken();
        flash = null;
    }
}
