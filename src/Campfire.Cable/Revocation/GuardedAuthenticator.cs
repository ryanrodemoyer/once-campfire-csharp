using Campfire.Cable.Server;
using Campfire.Data.Records;
using Microsoft.AspNetCore.Http;

namespace Campfire.Cable.Revocation;

/// <summary>
/// <c>ApplicationCable::Connection#connect</c>, settled against any revocation of the user still
/// in its transaction (<see cref="RevocationGuard"/>).
/// </summary>
sealed class GuardedAuthenticator(RevocationGuard guard, ICableAuthenticator<User> inner) : ICableAuthenticator<User>
{
    public async ValueTask<CableIdentity<User>?> ConnectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var identity = await inner.ConnectAsync(request, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        return await guard.SettleAsync(identity, () => inner.ConnectAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
