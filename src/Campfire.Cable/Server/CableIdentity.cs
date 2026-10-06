using Microsoft.AspNetCore.Http;

namespace Campfire.Cable.Server;

/// <summary>
/// What <c>ApplicationCable::Connection#connect</c> resolves a request to: the connection's
/// <c>current_user</c> (<c>identified_by :current_user</c>) and its connection identifier, which
/// remote disconnects address (<c>current_user.to_global_id.to_s</c>).
/// </summary>
public sealed record CableIdentity<TUser>(TUser CurrentUser, string ConnectionIdentifier)
    where TUser : class;

/// <summary>
/// <c>ApplicationCable::Connection#connect</c>: the identity for the upgrade request, or null for
/// <c>reject_unauthorized_connection</c>.
/// </summary>
public interface ICableAuthenticator<TUser>
    where TUser : class
{
    ValueTask<CableIdentity<TUser>?> ConnectAsync(HttpRequest request, CancellationToken cancellationToken);
}
