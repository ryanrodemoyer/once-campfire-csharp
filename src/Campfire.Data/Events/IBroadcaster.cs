namespace Campfire.Data.Events;

// `ActionCable.server.broadcast(stream, payload)`: delivers `payload`, already encoded, to every
// connection streaming from `stream`. Turbo stream broadcasts (`broadcast_append_to room,
// :messages`) and the unread-room fanout (message/broadcasts.rb) go through it. The cable server
// (RT lane) implements it; RecordingSeams records it for tests.
public interface IBroadcaster
{
    void Broadcast(string stream, string payload);
}
