using Campfire.Data.Records;
using Campfire.Web.ResponseCache;

namespace Campfire.Web.Pipeline;

// reference/app/controllers/concerns/authentication/session_lookup.rb, and reset_session, which
// every controller has (Active Storage's included).
public abstract partial class Controller
{
    /// <summary>The user <see cref="RecordSnapshots.SessionAsync"/> rebuilt with the session, if it did.</summary>
    protected User? RecordCachedUser { get; private set; }

    /// <summary>
    /// <c>Authentication::SessionLookup#find_session_by_cookie</c>: the session whose token the
    /// signed <c>session_token</c> cookie holds. A hit rebuilds the user too, so authentication
    /// does not query it again (reference/app/controllers/concerns/authentication/session_lookup.rb).
    /// </summary>
    public async ValueTask<Session?> FindSessionByCookieAsync()
    {
        if (Cookies.Signed["session_token"] is not { } token)
        {
            RecordCachedUser = null;
            return null;
        }
        var found = await RecordSnapshots.SessionAsync(this, token).ConfigureAwait(false);
        RecordCachedUser = found.Session is null ? null : found.User;
        return found.Session;
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
