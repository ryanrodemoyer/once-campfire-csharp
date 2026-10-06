using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Cable.PubSub;
using Campfire.RailsCompat.Crypto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Campfire.Cable.Server;

/// <summary>
/// <c>ActionCable::Connection::Base</c> and <c>Connection::Subscriptions</c> for one socket.
/// </summary>
/// <remarks>
/// The request's task reads the socket and handles commands one at a time, in arrival order. A
/// writer task owns sending: everything for the client (replies, broadcasts, pings) goes through
/// one bounded outbox, so a client that stops reading fills it and is disconnected with
/// <c>reconnect: true</c> instead of growing memory or silently missing messages.
/// </remarks>
internal sealed class CableConnection<TUser> : IFrameSink
    where TUser : class
{
    static readonly Frame WelcomeFrame = new(CableProtocol.Welcome());

    readonly CableServer<TUser> server;
    readonly WebSocket socket;
    readonly System.Threading.Channels.Channel<Frame> outbox;
    // Keyed by the raw identifier string, in subscription order (a Ruby hash).
    readonly List<Channel<TUser>> subscriptions = [];
    readonly TaskCompletionSource readerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CloseRequest? close;
    CableIdentity<TUser>? identity;

    public CableConnection(CableServer<TUser> server, WebSocket socket)
    {
        this.server = server;
        this.socket = socket;
        outbox = System.Threading.Channels.Channel.CreateBounded<Frame>(new System.Threading.Channels.BoundedChannelOptions(server.Config.QueueCapacity)
        {
            SingleReader = true,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
        });
    }

    ILogger Logger => server.Logger;

    /// <summary>
    /// <c>handle_open</c>, the command loop, then <c>handle_close</c>. Returns once the socket is
    /// closed and every subscription has been unsubscribed.
    /// </summary>
    public async Task RunAsync(HttpRequest request, CancellationToken aborted)
    {
        identity = await server.Authenticator.ConnectAsync(request, aborted).ConfigureAwait(false);
        if (identity is null)
        {
            CableLog.Unauthorized(Logger);
            RequestClose(CloseRequest.Disconnect(DisconnectReason.Unauthorized, false));
            await Task.WhenAll(WriteLoopAsync(), ReadLoopAsync(dispatch: false, aborted)).ConfigureAwait(false);
            return;
        }

        using var internalChannel = SubscribeToInternalChannel();
        // A ban or sign-out that disconnected this user between the check above and that
        // subscription went unheard, so check again now that it would be heard (Rails has this gap).
        if (internalChannel is not null && await server.Authenticator.ConnectAsync(request, aborted).ConfigureAwait(false) is null)
        {
            RequestClose(CloseRequest.Disconnect(DisconnectReason.Unauthorized, false));
            await Task.WhenAll(WriteLoopAsync(), ReadLoopAsync(dispatch: false, aborted)).ConfigureAwait(false);
            return;
        }

        Deliver(WelcomeFrame);
        server.Register(this);
        try
        {
            await Task.WhenAll(WriteLoopAsync(), ReadLoopAsync(dispatch: true, aborted)).ConfigureAwait(false);
        }
        finally
        {
            server.Unregister(this);
            await HandleCloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Queues a frame for the client; a full queue means it's lagging.</summary>
    public void Deliver(Frame frame)
    {
        if (!outbox.Writer.TryWrite(frame) && close is null)
        {
            RequestClose(CloseRequest.Lagged());
        }
    }

    /// <summary>Closes the socket for <paramref name="request"/>, unless it's already closing.</summary>
    public void RequestClose(CloseRequest request)
    {
        if (Interlocked.CompareExchange(ref close, request, null) is null)
        {
            outbox.Writer.TryComplete();
        }
    }

    /// <summary>
    /// <c>InternalChannel#subscribe_to_internal_channel</c>: remote disconnects for this
    /// connection's identifier arrive as raw payloads.
    /// </summary>
    HubSubscription? SubscribeToInternalChannel()
    {
        var identifier = identity!.ConnectionIdentifier;
        return string.IsNullOrEmpty(identifier) ? null : server.Hub.Subscribe(CableServer.InternalChannel(identifier), null, new InternalChannelSink(this));
    }

    /// <summary><c>InternalChannel#process_internal_message</c>.</summary>
    void ProcessInternalMessage(string message)
    {
        if (RailsJson.TryParse(message, out var node) && node is JsonObject data && data["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && (string?)data["type"] == "disconnect")
        {
            CableLog.RemovingConnection(Logger, identity!.ConnectionIdentifier);
            var reconnect = data.TryGetPropertyValue("reconnect", out var value) ? value : JsonValue.Create(true);
            RequestClose(CloseRequest.Disconnect(DisconnectReason.Remote, reconnect));
        }
    }

    /// <summary>
    /// Sends queued frames in order until a close is requested, then closes: the disconnect
    /// message and a normal close (1000, no reason, as <c>ClientSocket#close</c>), waiting briefly
    /// for the client to finish the handshake.
    /// </summary>
    async Task WriteLoopAsync()
    {
        try
        {
            while (close is null && await outbox.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (close is null && outbox.Reader.TryRead(out var frame))
                {
                    await socket.SendAsync(frame.Utf8, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            if (close is not { } request)
            {
                return;
            }
            if (request.DisconnectFrame is { } disconnect)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(disconnect), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(request.Status, null, CancellationToken.None).ConfigureAwait(false);
            }
            if (!request.Reply)
            {
                await readerDone.Task.WaitAsync(server.Config.CloseTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    /// <summary>
    /// Reads whole messages and dispatches each, until the client closes or the socket fails.
    /// Without <paramref name="dispatch"/> (a rejected connection) it only waits for the close.
    /// </summary>
    async Task ReadLoopAsync(bool dispatch, CancellationToken aborted)
    {
        var buffer = new byte[4096];
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), aborted).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RequestClose(CloseRequest.Answer(socket.CloseStatus));
                    break;
                }
                if (message.WrittenCount + result.Count > server.Config.MaxMessageBytes)
                {
                    RequestClose(CloseRequest.Fail(WebSocketCloseStatus.MessageTooBig));
                    break;
                }
                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }
                if (dispatch && close is null)
                {
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        CableLog.NonStringMessage(Logger);
                    }
                    else
                    {
                        await DispatchAsync(Encoding.UTF8.GetString(message.WrittenSpan)).ConfigureAwait(false);
                    }
                }
                message.ResetWrittenCount();
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The socket failed or the client went away (an invalid frame has been answered with
            // its close code already): nothing more to say to it.
            RequestClose(CloseRequest.Fail(WebSocketCloseStatus.ProtocolError));
        }
        finally
        {
            readerDone.TrySetResult();
        }
    }

    /// <summary>
    /// <c>Subscriptions#execute_command</c>. Anything malformed raises in Rails, which is logged
    /// and otherwise ignored; the connection stays open.
    /// </summary>
    async Task DispatchAsync(string text)
    {
        if (!RailsJson.TryParse(text, out var node) || node is not JsonObject data)
        {
            CableLog.CouldNotExecute(Logger, text, "not a JSON object");
            return;
        }
        try
        {
            switch (StringValue(data["command"]))
            {
                case "subscribe":
                    await AddAsync(data).ConfigureAwait(false);
                    break;
                case "unsubscribe":
                    await RemoveAsync(data).ConfigureAwait(false);
                    break;
                case "message":
                    await PerformActionAsync(data).ConfigureAwait(false);
                    break;
                default:
                    CableLog.UnrecognizedCommand(Logger, text);
                    break;
            }
        }
#pragma warning disable CA1031 // A channel callback's exception is logged and the connection carries on, as in Rails.
        catch (Exception error)
#pragma warning restore CA1031
        {
            CableLog.CommandFailed(Logger, error, text);
        }
    }

    /// <summary><c>Subscriptions#add</c>. A repeated identifier (byte for byte) is ignored without a reply.</summary>
    async Task AddAsync(JsonObject data)
    {
        if (StringValue(data["identifier"]) is not { } identifier)
        {
            CableLog.CouldNotExecute(Logger, data.ToJsonString(), "missing identifier");
            return;
        }
        if (!RailsJson.TryParse(identifier, out var node) || node is not JsonObject parameters)
        {
            CableLog.CouldNotExecute(Logger, data.ToJsonString(), "invalid identifier");
            return;
        }
        if (Find(identifier) is not null)
        {
            return;
        }
        // Bounds on what one socket can make the server hold (Rails has none). A page subscribes
        // to six channels with identifiers of a few hundred bytes.
        if (subscriptions.Count >= server.Config.MaxSubscriptions || Encoding.UTF8.GetByteCount(identifier) > server.Config.MaxIdentifierBytes)
        {
            CableLog.CouldNotExecute(Logger, data.ToJsonString(), "subscription limit reached");
            return;
        }
        var requested = StringValue(parameters["channel"]) ?? "";
        if (server.FindChannel(requested) is not var (className, factory))
        {
            CableLog.ChannelNotFound(Logger, requested);
            return;
        }

        var channel = factory();
        channel.Attach(server, className, identifier, identity!.CurrentUser);
        subscriptions.Add(channel);
        await SubscribeToChannelAsync(channel).ConfigureAwait(false);
    }

    /// <summary><c>Channel::Base#subscribe_to_channel</c>.</summary>
    async Task SubscribeToChannelAsync(Channel<TUser> channel)
    {
        try
        {
            await channel.SubscribedAsync().ConfigureAwait(false);
        }
        finally
        {
            DeliverTransmissions(channel);
        }

        if (channel.SubscriptionRejected)
        {
            await RemoveSubscriptionAsync(channel).ConfigureAwait(false);
            Deliver(new Frame(CableProtocol.Rejection(channel.Identifier)));
        }
        else
        {
            Deliver(new Frame(CableProtocol.Confirmation(channel.Identifier)));
            channel.StartStreams(this);
        }
    }

    /// <summary><c>Subscriptions#remove</c>: no reply either way.</summary>
    async Task RemoveAsync(JsonObject data)
    {
        if (Find(StringValue(data["identifier"])) is { } channel)
        {
            await RemoveSubscriptionAsync(channel).ConfigureAwait(false);
        }
        else
        {
            CableLog.SubscriptionNotFound(Logger, data["identifier"]?.ToJsonString());
        }
    }

    /// <summary><c>Subscriptions#remove_subscription</c> → <c>Channel::Base#unsubscribe_from_channel</c>.</summary>
    async Task RemoveSubscriptionAsync(Channel<TUser> channel)
    {
        subscriptions.Remove(channel);
        channel.MarkUnsubscribed();
        try
        {
            await channel.UnsubscribedAsync().ConfigureAwait(false);
        }
        finally
        {
            channel.StopAllStreams();
            DeliverTransmissions(channel);
        }
    }

    /// <summary><c>Subscriptions#perform_action</c> → <c>Channel::Base#perform_action</c>.</summary>
    async Task PerformActionAsync(JsonObject data)
    {
        if (Find(StringValue(data["identifier"])) is not { } channel)
        {
            CableLog.SubscriptionNotFound(Logger, data["identifier"]?.ToJsonString());
            return;
        }
        if (StringValue(data["data"]) is not { } encoded || !RailsJson.TryParse(encoded, out var node) || node is not JsonObject payload)
        {
            CableLog.CouldNotExecute(Logger, data.ToJsonString(), "invalid data");
            return;
        }
        // `(data["action"].presence || :receive).to_s`
        string action;
        switch (payload["action"])
        {
            case null:
                action = "receive";
                break;
            case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                var name = value.GetValue<string>();
                action = string.IsNullOrWhiteSpace(name) ? "receive" : name;
                break;
            default:
                CableLog.CouldNotExecute(Logger, data.ToJsonString(), "invalid action");
                return;
        }

        try
        {
            if (!await channel.PerformAsync(action, payload).ConfigureAwait(false))
            {
                CableLog.UnableToProcess(Logger, channel.ClassName, action);
            }
        }
        finally
        {
            DeliverTransmissions(channel);
            channel.StartStreams(this);
        }
    }

    /// <summary><c>Connection::Base#handle_close</c>: unsubscribe everything.</summary>
    async Task HandleCloseAsync()
    {
        while (subscriptions.Count > 0)
        {
            var channel = subscriptions[0];
            try
            {
                await RemoveSubscriptionAsync(channel).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Logged like any callback's exception; the rest still unsubscribe.
            catch (Exception error)
#pragma warning restore CA1031
            {
                CableLog.UnsubscribeFailed(Logger, error, channel.Identifier);
            }
        }
    }

    void DeliverTransmissions(Channel<TUser> channel)
    {
        foreach (var transmission in channel.Transmissions)
        {
            Deliver(new Frame(transmission));
        }
        channel.Transmissions.Clear();
    }

    Channel<TUser>? Find(string? identifier) =>
        identifier is null ? null : subscriptions.Find(channel => channel.Identifier == identifier);

    static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The internal channel's payloads are raw, not frames for the client.</summary>
    sealed class InternalChannelSink(CableConnection<TUser> connection) : IFrameSink
    {
        public void Deliver(Frame frame) => connection.ProcessInternalMessage(frame.Text);
    }
}
