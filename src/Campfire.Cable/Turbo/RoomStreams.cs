namespace Campfire.Cable.Turbo;

/// <summary>
/// <c>RoomMessagesChannel.guarded_stream?</c> and the <c>RoomStreamsAreAuthorized</c> guard
/// prepended onto <c>Turbo::StreamsChannel</c>
/// (reference/app/channels/room_messages_channel.rb,
/// reference/app/channels/concerns/room_streams_are_authorized.rb).
/// A room's message stream is only served by <c>RoomMessagesChannel</c> (RT03); the stock
/// channel turns those names away, whoever is asking.
/// </summary>
public static class RoomStreams
{
    /// <summary><c>RoomMessagesChannel::STREAM_SUFFIX</c>.</summary>
    public const string Suffix = "messages";

    /// <summary>
    /// True for the stream names <c>RoomMessagesChannel</c> exists to guard.
    /// <c>nil.to_s</c> is <c>""</c>, which is not guarded.
    /// </summary>
    public static bool IsGuarded(string streamName)
    {
        ArgumentNullException.ThrowIfNull(streamName);
        var parts = streamName.Split(':', 2);
        return parts.Length == 2 && parts[1] == Suffix;
    }
}
