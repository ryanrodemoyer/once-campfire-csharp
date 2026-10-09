using Campfire.Cable.Server;
using Campfire.Data.Events;
using Campfire.Data.Records;

namespace Campfire.Cable.Revocation;

/// <summary>
/// <c>User#close_remote_connections(reconnect:)</c> (reference/app/models/user.rb):
/// <c>ActionCable.server.remote_connections.where(current_user: self).disconnect reconnect:</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A destroyed membership (<c>after_destroy_commit { user.reset_remote_connections }</c>,
/// reference/app/models/membership.rb) disconnects with <c>reconnect: true</c>. The browser
/// reconnects and replays its subscriptions, and the channels turn away the rooms it lost.</item>
/// <item><c>User#deactivate</c> and <c>User::Bannable#ban</c> disconnect with
/// <c>reconnect: false</c>, and the sessions they delete keep the browser out.</item>
/// </list>
/// Each of the user's connections is sent <c>{"type":"disconnect","reason":"remote","reconnect":…}</c>
/// and closed, which unsubscribes every channel. It acts at once: by the time
/// <see cref="Disconnect"/> returns, those connections send nothing more, so no later publication
/// reaches them.
/// </remarks>
public sealed class CableConnectionRevoker : IConnectionRevoker
{
    readonly CableServer<User> server;
    readonly RevocationGuard guard;

    public CableConnectionRevoker(CableServer<User> server, RevocationGuard guard)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(guard);
        this.server = server;
        this.guard = guard;
    }

    /// <summary>Disconnects every connection signed in as the user.</summary>
    public void Disconnect(long userId, bool reconnect)
    {
        guard.Revoked(userId);
        server.Disconnect(SessionCookieAuthenticator.ConnectionIdentifier(userId), reconnect);
    }
}
