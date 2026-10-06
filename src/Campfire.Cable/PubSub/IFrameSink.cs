namespace Campfire.Cable.PubSub;

/// <summary>
/// Where a <see cref="PubSubHub"/> subscription delivers. Called on the broadcaster's thread, so it
/// must not block: a connection queues the frame, or gives up on a client that has fallen behind.
/// </summary>
public interface IFrameSink
{
    void Deliver(Frame frame);
}
