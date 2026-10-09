using System.Collections.Concurrent;
using Campfire.Cable.Server;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Cable.Revocation;

/// <summary>
/// Keeps a revoked user's connections from outliving the revocation.
/// </summary>
/// <remarks>
/// <c>User#deactivate</c> and <c>User::Bannable#ban</c> close the user's connections inside their
/// transaction, before the sessions are gone (reference/app/models/user.rb,
/// reference/app/models/user/bannable.rb). A connection that authenticates in that window reads
/// the last commit, finds the session, and would stay open, streaming its rooms, after the
/// revocation commits (Rails has this gap). So once a user has been revoked, the next connection
/// for them waits for the writer, which runs the revoking transaction to its end first, and
/// authenticates again against what it committed. Subscriptions come after the welcome, so they
/// are authorized against the committed state too, and every later publication reaches only
/// subscriptions that state allows.
/// <code>
/// var guard = new RevocationGuard(database);
/// var server = AppChannels.Register(CableServer.Builder(config, guard.Authenticate(new SessionCookieAuthenticator(database, keys))), database, keys).Build();
/// IConnectionRevoker revoker = guard.RevokerFor(server);
/// </code>
/// </remarks>
public sealed class RevocationGuard
{
    readonly SqliteDatabase database;
    // User id → the revocation's sequence number, so settling one never forgets a later one.
    readonly ConcurrentDictionary<long, long> pending = new();
    long sequence;

    public RevocationGuard(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary>Users revoked since a connection for them last authenticated.</summary>
    public int PendingCount => pending.Count;

    /// <summary>Wraps <c>ApplicationCable::Connection#connect</c> so it waits out a revocation.</summary>
    public ICableAuthenticator<User> Authenticate(ICableAuthenticator<User> inner) => new GuardedAuthenticator(this, inner);

    /// <summary>The <c>close_remote_connections</c> seam for <paramref name="server"/>.</summary>
    public CableConnectionRevoker RevokerFor(CableServer<User> server) => new(server, this);

    /// <summary>Marks the user revoked. Call before their connections are told, so none slips between.</summary>
    internal void Revoked(long userId) => pending[userId] = Interlocked.Increment(ref sequence);

    /// <summary>
    /// The user's identity once any revocation of theirs has finished: authenticated again after
    /// the writer has run every transaction that revoked them.
    /// </summary>
    internal async ValueTask<CableIdentity<User>?> SettleAsync(CableIdentity<User> identity, Func<ValueTask<CableIdentity<User>?>> authenticate, CancellationToken cancellationToken)
    {
        var userId = identity.CurrentUser.Id;
        if (!pending.TryGetValue(userId, out var revoked))
        {
            return identity;
        }

        // Writes run one at a time, in order: this one starts after the revoking transaction ends.
        await database.WriteAsync(_ => { }, cancellationToken).ConfigureAwait(false);
        pending.TryRemove(KeyValuePair.Create(userId, revoked));
        return await authenticate().ConfigureAwait(false);
    }
}
