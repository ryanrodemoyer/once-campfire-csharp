using Campfire.Cable.Server;
using Campfire.Data.Records;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>HeartbeatChannel</c> (reference/app/channels/heartbeat_channel.rb): confirms and does nothing else.
/// </summary>
public sealed class HeartbeatChannel : Channel<User>;
