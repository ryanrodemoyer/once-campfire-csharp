using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Microsoft.AspNetCore.Http;

namespace Campfire.Cable.Server;

/// <summary>
/// <c>ApplicationCable::Connection#connect</c> (reference/app/channels/application_cable/connection.rb):
/// the user of the session the signed <c>session_token</c> cookie names
/// (<c>Authentication::SessionLookup#find_session_by_cookie</c>), else
/// <c>reject_unauthorized_connection</c>. The same cookie the HTTP app reads, so a browser
/// signed in under Rails stays connected.
/// </summary>
public sealed class SessionCookieAuthenticator(SqliteDatabase database, KeyGenerator keys) : ICableAuthenticator<User>
{
    public async ValueTask<CableIdentity<User>?> ConnectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cookies = CookieJar.FromHeaders(request.Headers.Cookie.Select(value => value ?? ""), keys);
        if (cookies.Signed["session_token"] is not { } token)
        {
            return null;
        }
        // `verified_session.user`
        var user = await database.ReadAsync(session => Sessions.FindByToken(session, token) is { } verified ? Users.Find(session, verified.UserId) : null, cancellationToken).ConfigureAwait(false);
        return user is null ? null : new CableIdentity<User>(user, ConnectionIdentifier(user.Id));
    }

    /// <summary>
    /// <c>connection_identifier</c> for <c>identified_by :current_user</c>: the user's GlobalID,
    /// which <c>remote_connections.where(current_user: user)</c> addresses.
    /// </summary>
    public static string ConnectionIdentifier(long userId) => GlobalId.Create("User", userId).ToString();
}
