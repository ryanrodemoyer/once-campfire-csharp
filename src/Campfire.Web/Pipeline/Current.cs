using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>Current</c> (reference/app/models/current.rb): the request's <c>session</c>, <c>user</c>
/// and <c>request</c>, fresh for every request. <see cref="SetSession"/> also sets
/// <see cref="User"/> to the session's user, as <c>Current#session=</c> does.
/// </summary>
public sealed class Current(Controller request)
{
    Account? account;
    bool accountLoaded;

    /// <summary><c>Current.request</c> (set by <c>SetCurrentRequest</c>).</summary>
    public Controller Request { get; } = request;

    /// <summary><c>Current.session</c>: the signed-in browser's row.</summary>
    public Session? Session { get; private set; }

    /// <summary><c>Current.user</c>: the session's user, or the bot a bot key authenticated.</summary>
    public User? User { get; set; }

    /// <summary><c>Current.session = session</c>, which also sets <c>Current.user = session.user</c>.</summary>
    public void SetSession(Session? session, User? user)
    {
        Session = session;
        if (session is not null)
        {
            User = user;
        }
    }

    /// <summary>
    /// <c>Current.account</c>: <c>Account.first</c>. Rails queries it on every call; it's read once
    /// per request here, so call <see cref="ReloadAccount"/> after changing it.
    /// </summary>
    public async ValueTask<Account?> AccountAsync()
    {
        if (!accountLoaded)
        {
            account = await Request.ReadAsync(Accounts.First).ConfigureAwait(false);
            accountLoaded = true;
        }
        return account;
    }

    public void ReloadAccount() => accountLoaded = false;
}
