using Campfire.Cable.Server;

namespace Campfire.Cable.PubSub;

/// <summary>
/// The in-process replacement for the Redis subscription adapter (reference/config/cable.yml):
/// broadcastings are created on first subscribe and dropped with their last subscriber.
/// </summary>
/// <remarks>
/// Payloads are already-encoded JSON, as they are on the Redis wire. Subscribers that wrap a
/// payload identically (the same encoded channel identifier) form one group, and each broadcast
/// builds that group's <c>{"identifier":…,"message":…}</c> frame once for all of them. Raw
/// subscribers (the connection's internal channel) get the payload itself. Delivery never blocks:
/// each connection queues into its own bounded outbox.
/// </remarks>
public sealed class PubSubHub
{
    readonly Lock gate = new();
    readonly Dictionary<string, List<Group>> streams = new(StringComparer.Ordinal);

    /// <summary>
    /// Publishes an encoded payload to every current subscriber of <paramref name="broadcasting"/>.
    /// Returns how many received it.
    /// </summary>
    public int Broadcast(string broadcasting, string payload)
    {
        ArgumentNullException.ThrowIfNull(broadcasting);
        ArgumentNullException.ThrowIfNull(payload);
        Group[] groups;
        lock (gate)
        {
            if (!streams.TryGetValue(broadcasting, out var list))
            {
                return 0;
            }
            groups = [.. list];
        }

        var receivers = 0;
        foreach (var group in groups)
        {
            var subscribers = group.Subscribers;
            if (subscribers.Length == 0)
            {
                continue;
            }
            var frame = group.Identifier is { } identifier ? new Frame(CableProtocol.Message(identifier, payload)) : new Frame(payload);
            foreach (var subscriber in subscribers)
            {
                subscriber.Sink.Deliver(frame);
            }
            receivers += subscribers.Length;
        }
        return receivers;
    }

    /// <summary>
    /// Subscribes <paramref name="sink"/> to <paramref name="broadcasting"/>: each payload arrives
    /// wrapped as a message frame for the encoded channel <paramref name="encodedIdentifier"/>, or
    /// raw when it's null. Dispose the subscription to stop.
    /// </summary>
    public HubSubscription Subscribe(string broadcasting, string? encodedIdentifier, IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(broadcasting);
        ArgumentNullException.ThrowIfNull(sink);
        lock (gate)
        {
            if (!streams.TryGetValue(broadcasting, out var groups))
            {
                groups = [];
                streams[broadcasting] = groups;
            }
            var group = groups.Find(g => g.Identifier == encodedIdentifier);
            if (group is null)
            {
                group = new Group(encodedIdentifier);
                groups.Add(group);
            }
            var subscription = new HubSubscription(this, broadcasting, group, sink);
            group.Subscribers = [.. group.Subscribers, subscription];
            return subscription;
        }
    }

    /// <summary>Broadcastings with at least one subscriber.</summary>
    public int StreamCount
    {
        get
        {
            lock (gate)
            {
                return streams.Count;
            }
        }
    }

    internal void Release(HubSubscription subscription)
    {
        lock (gate)
        {
            var group = subscription.Group;
            group.Subscribers = Array.FindAll(group.Subscribers, s => s != subscription);
            if (group.Subscribers.Length > 0 || !streams.TryGetValue(subscription.Broadcasting, out var groups))
            {
                return;
            }
            groups.Remove(group);
            if (groups.Count == 0)
            {
                streams.Remove(subscription.Broadcasting);
            }
        }
    }

    internal sealed class Group(string? identifier)
    {
        public string? Identifier { get; } = identifier;

        // Replaced, never mutated, under the hub's lock, so a broadcast can read it without one.
        public volatile HubSubscription[] Subscribers = [];
    }
}

/// <summary>One sink's subscription to one broadcasting. Disposing it stops delivery.</summary>
public sealed class HubSubscription : IDisposable
{
    readonly PubSubHub hub;
    int disposed;

    internal HubSubscription(PubSubHub hub, string broadcasting, PubSubHub.Group group, IFrameSink sink)
    {
        this.hub = hub;
        Broadcasting = broadcasting;
        Group = group;
        Sink = sink;
    }

    public string Broadcasting { get; }

    internal PubSubHub.Group Group { get; }

    internal IFrameSink Sink { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            hub.Release(this);
        }
    }
}
