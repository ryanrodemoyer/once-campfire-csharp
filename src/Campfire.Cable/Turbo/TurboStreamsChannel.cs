using Campfire.Cable.Server;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Cable.Turbo;

/// <summary>
/// <c>Turbo::StreamsChannel</c> with <c>RoomStreamsAreAuthorized</c> prepended
/// (reference/config/initializers/turbo_streams_authorization.rb). A subscription carries a
/// <c>signed_stream_name</c>; an unverified name is rejected, and a verified room message
/// stream (<c>&lt;room gid param&gt;:messages</c>) is rejected here so it cannot bypass
/// <c>RoomMessagesChannel</c>.
/// </summary>
public sealed class TurboStreamsChannel<TUser> : Channel<TUser>
    where TUser : class
{
    readonly KeyGenerator keys;

    public TurboStreamsChannel(KeyGenerator keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        this.keys = keys;
    }

    /// <summary><c>subscribed</c>: the guard runs, then the stock channel.</summary>
    public override ValueTask SubscribedAsync()
    {
        var streamName = TurboStreams.VerifiedStreamName(keys, Params);
        // The guard sees a failed verification as "" (`nil.to_s`), which is not a message stream.
        if (RoomStreams.IsGuarded(streamName ?? ""))
        {
            Reject();
        }
        else if (streamName is null)
        {
            Reject();
        }
        else
        {
            StreamFrom(streamName);
        }

        return ValueTask.CompletedTask;
    }
}
