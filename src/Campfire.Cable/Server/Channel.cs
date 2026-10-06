using System.Text.Json.Nodes;
using Campfire.Cable.PubSub;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Cable.Server;

/// <summary>
/// <c>ActionCable::Channel::Base</c>: one instance per subscription, built by the factory the
/// channel class is registered with (<see cref="CableServerBuilder{TUser}.Channel"/>) and driven by
/// its connection, one command at a time.
/// </summary>
/// <remarks>
/// Rails' <c>on_subscribe</c>/<c>on_unsubscribe</c> callbacks run after <c>subscribed</c> and
/// <c>unsubscribed</c>, so put them at the end of those methods (guarding on
/// <see cref="SubscriptionRejected"/> where the Ruby says <c>unless: :subscription_rejected?</c>).
/// An exception escaping a callback is logged and nothing is sent: a subscription whose
/// <c>subscribed</c> raised is neither confirmed nor rejected, and stays registered.
/// </remarks>
public abstract class Channel<TUser>
    where TUser : class
{
    readonly List<(string Broadcasting, HubSubscription? Subscription)> streams = [];
    CableServer<TUser>? server;
    string? encodedIdentifier;
    JsonObject? parameters;

    /// <summary>The registered class name, whatever spelling the client resolved it with.</summary>
    public string ClassName { get; private set; } = "";

    /// <summary>The raw identifier the client subscribed with.</summary>
    public string Identifier { get; private set; } = "";

    /// <summary><c>identified_by :current_user</c>.</summary>
    public TUser CurrentUser { get; private set; } = null!;

    public CableServer<TUser> Server => server ?? throw new InvalidOperationException("The channel isn't attached to a connection");

    /// <summary><c>params</c>: the decoded identifier, <c>channel</c> included.</summary>
    public JsonObject Params => parameters ??= RailsJson.TryParse(Identifier, out var node) && node is JsonObject obj ? obj : [];

    /// <summary><c>subscription_rejected?</c></summary>
    public bool SubscriptionRejected { get; private set; }

    /// <summary>The broadcastings this subscription streams from, in the order it started them.</summary>
    public IEnumerable<string> Streams => streams.Select(s => s.Broadcasting);

    /// <summary><c>channel_name</c> for this channel's class.</summary>
    public string ChannelName => ChannelNaming.ChannelName(ClassName);

    internal bool Unsubscribed { get; private set; }

    internal List<string> Transmissions { get; } = [];

    /// <summary><c>subscribed</c>.</summary>
    public virtual ValueTask SubscribedAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// <c>unsubscribed</c>. Also runs when a subscription is rejected, as in Rails
    /// (<c>reject_subscription</c> removes the subscription, which unsubscribes it).
    /// </summary>
    public virtual ValueTask UnsubscribedAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A <c>perform</c> from the client. <paramref name="action"/> is <c>data["action"]</c>, or
    /// <c>"receive"</c> when that's blank. Return false when it isn't one of the channel's public
    /// actions, which Rails logs as "Unable to process".
    /// </summary>
    public virtual ValueTask<bool> PerformAsync(string action, JsonObject data) => ValueTask.FromResult(false);

    /// <summary><c>stream_from</c>. Delivery starts once the current command's own frames are queued.</summary>
    public void StreamFrom(string broadcasting)
    {
        ArgumentNullException.ThrowIfNull(broadcasting);
        if (!Unsubscribed)
        {
            streams.Add((broadcasting, null));
        }
    }

    /// <summary><c>stream_for</c>.</summary>
    public void StreamFor(params IEnumerable<string> broadcastables) => StreamFrom(BroadcastingFor(broadcastables));

    /// <summary><c>stream_or_reject_for</c>.</summary>
    public void StreamOrRejectFor(IEnumerable<string>? broadcastables)
    {
        if (broadcastables is null)
        {
            Reject();
        }
        else
        {
            StreamFor(broadcastables);
        }
    }

    /// <summary><c>stop_stream_from</c>.</summary>
    public void StopStreamFrom(string broadcasting)
    {
        for (var i = streams.Count - 1; i >= 0; i--)
        {
            if (streams[i].Broadcasting == broadcasting)
            {
                streams[i].Subscription?.Dispose();
                streams.RemoveAt(i);
            }
        }
    }

    /// <summary><c>stop_all_streams</c>.</summary>
    public void StopAllStreams()
    {
        foreach (var (_, subscription) in streams)
        {
            subscription?.Dispose();
        }
        streams.Clear();
    }

    /// <summary><c>reject</c>.</summary>
    public void Reject() => SubscriptionRejected = true;

    /// <summary><c>transmit</c>: <c>{"identifier":…,"message":…}</c> to this subscriber only.</summary>
    public void Transmit(JsonNode? message) =>
        Transmissions.Add(CableProtocol.Message(EncodedIdentifier, RailsJson.Encode(message)));

    /// <summary><c>broadcasting_for</c> for this channel's class.</summary>
    public string BroadcastingFor(params IEnumerable<string> broadcastables) =>
        ChannelNaming.BroadcastingFor(ClassName, broadcastables);

    /// <summary><c>broadcast_to</c> for this channel's class.</summary>
    public int BroadcastTo(IEnumerable<string> broadcastables, JsonNode? message) =>
        Server.Broadcast(BroadcastingFor(broadcastables), message);

    string EncodedIdentifier => encodedIdentifier ??= CableProtocol.EncodeString(Identifier);

    internal void Attach(CableServer<TUser> server, string className, string identifier, TUser currentUser)
    {
        this.server = server;
        ClassName = className;
        Identifier = identifier;
        CurrentUser = currentUser;
    }

    /// <summary>Starts delivering the streams the last callback started, into <paramref name="sink"/>.</summary>
    internal void StartStreams(IFrameSink sink)
    {
        for (var i = 0; i < streams.Count; i++)
        {
            if (streams[i].Subscription is null)
            {
                streams[i] = (streams[i].Broadcasting, Server.Hub.Subscribe(streams[i].Broadcasting, EncodedIdentifier, sink));
            }
        }
    }

    internal void MarkUnsubscribed() => Unsubscribed = true;
}

/// <summary>
/// A channel with no callbacks: <c>ApplicationCable::Channel</c> itself and
/// <c>HeartbeatChannel</c> subscribe, confirm and do nothing else.
/// </summary>
public sealed class EmptyChannel<TUser> : Channel<TUser>
    where TUser : class;
