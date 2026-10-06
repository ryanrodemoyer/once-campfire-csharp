namespace Campfire.Data.Events;

// `ActionCable.server.remote_connections.where(current_user: user).disconnect(reconnect:)`
// (user.rb `close_remote_connections`): closes every cable connection signed in as the user.
// With `reconnect`, the browser is told to connect again and re-authenticates (a membership
// change); without it, it stays away (deactivation, bans). Like Rails, it acts at once, even
// inside a transaction that later rolls back.
public interface IConnectionRevoker
{
    void Disconnect(long userId, bool reconnect);
}
