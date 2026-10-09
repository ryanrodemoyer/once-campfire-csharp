using Campfire.Cable.Server;
using Campfire.Cable.Turbo;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Cable.Channels;

/// <summary>
/// Every Campfire channel under its Ruby class name (reference/app/channels,
/// reference-rust <c>channels::register</c>). <c>RoomMessagesChannel</c> verifies signed stream
/// names with the same key as <c>Turbo::StreamsChannel</c>, which keeps the room-message guard
/// from RT02.
/// </summary>
/// <remarks>
/// The HTTP server does not mount <c>/cable</c> yet (P01, <c>ServerCommand</c>). Call
/// <see cref="Register"/> when it does.
/// </remarks>
public static class AppChannels
{
    /// <summary>Registers the channels. Returns <paramref name="builder"/> for chaining.</summary>
    public static CableServerBuilder<User> Register(CableServerBuilder<User> builder, SqliteDatabase database, KeyGenerator keys)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(keys);
        return builder
            .Channel("ApplicationCable::Channel", () => new EmptyChannel<User>())
            .Channel("HeartbeatChannel", () => new HeartbeatChannel())
            .Channel("PresenceChannel", () => new PresenceChannel(database))
            .Channel("ReadRoomsChannel", () => new ReadRoomsChannel())
            .Channel("RoomChannel", () => new RoomChannel(database))
            .Channel("RoomMessagesChannel", () => new RoomMessagesChannel(database, keys))
            .Channel("TypingNotificationsChannel", () => new TypingNotificationsChannel(database))
            .Channel("UnreadRoomsChannel", () => new UnreadRoomsChannel())
            .Channel(TurboStreams.ClassName, () => new TurboStreamsChannel<User>(keys));
    }
}
