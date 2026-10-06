using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Campfire.Cable.PubSub;
using Campfire.Data.Events;
using Campfire.RailsCompat.Crypto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Cable.Server;

/// <summary>
/// <c>ActionCable::Server::Base</c>: the channel registry, broadcasting, the heartbeat, remote
/// disconnects and the <c>/cable</c> endpoint (<see cref="HandleAsync"/>). The app mounts it at
/// <see cref="CableProtocol.DefaultMountPath"/> behind <c>UseWebSockets</c>.
/// </summary>
public sealed class CableServer<TUser> : IBroadcaster, IDisposable
    where TUser : class
{
    readonly IReadOnlyDictionary<string, Func<Channel<TUser>>> channels;
    readonly ConcurrentDictionary<CableConnection<TUser>, byte> connections = new();
    readonly Lock heartbeatGate = new();
    ITimer? heartbeat;

    internal CableServer(CableConfig config, ICableAuthenticator<TUser> authenticator, IReadOnlyDictionary<string, Func<Channel<TUser>>> channels, TimeProvider clock, ILogger logger)
    {
        Config = config;
        Authenticator = authenticator;
        this.channels = channels;
        Clock = clock;
        Logger = logger;
    }

    public CableConfig Config { get; }

    public PubSubHub Hub { get; } = new();

    internal ICableAuthenticator<TUser> Authenticator { get; }

    internal TimeProvider Clock { get; }

    internal ILogger Logger { get; }

    /// <summary>Open connections.</summary>
    public int ConnectionCount => connections.Count;

    /// <summary>
    /// <c>ActionCable::Server::Base#call</c>. Anything that isn't a WebSocket upgrade from an
    /// allowed origin gets Rails' 404 "Page not found".
    /// </summary>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        StartHeartbeat();
        if (!context.WebSockets.IsWebSocketRequest || !AllowRequestOrigin(context.Request))
        {
            await PageNotFoundAsync(context.Response).ConfigureAwait(false);
            return;
        }

        var protocol = CableProtocol.NegotiateProtocol(context.Request.Headers.SecWebSocketProtocol);
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = protocol,
            DangerousEnableCompression = Config.EnableCompression,
            DisableServerContextTakeover = true,
            // Action Cable pings in its own frames; Rails sends no WebSocket-level pings.
            KeepAliveInterval = TimeSpan.Zero,
        }).ConfigureAwait(false);
        var connection = new CableConnection<TUser>(this, socket);
        await connection.RunAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary><c>ActionCable.server.broadcast(broadcasting, message)</c>, from encoded JSON.</summary>
    public void Broadcast(string stream, string payload) => BroadcastEncoded(stream, payload);

    /// <summary><c>ActionCable.server.broadcast(broadcasting, message)</c>. Returns the receivers.</summary>
    public int Broadcast(string broadcasting, JsonNode? message) => BroadcastEncoded(broadcasting, RailsJson.Encode(message));

    /// <summary><c>SomeChannel.broadcast_to(broadcastables, message)</c>.</summary>
    public int BroadcastTo(string className, IEnumerable<string> broadcastables, JsonNode? message) =>
        Broadcast(ChannelNaming.BroadcastingFor(className, broadcastables), message);

    /// <summary>
    /// <c>ActionCable.server.remote_connections.where(current_user: user).disconnect(reconnect:)</c>:
    /// every connection with this identifier is sent
    /// <c>{"type":"disconnect","reason":"remote","reconnect":…}</c> and closed. Returns how many.
    /// </summary>
    public int Disconnect(string connectionIdentifier, bool reconnect) =>
        Broadcast(CableServer.InternalChannel(connectionIdentifier), new JsonObject { ["type"] = "disconnect", ["reconnect"] = reconnect });

    /// <summary><c>ActionCable.server.restart</c>: closes every connection with <c>server_restart</c>.</summary>
    public void Restart()
    {
        foreach (var connection in connections.Keys)
        {
            connection.RequestClose(CloseRequest.Disconnect(DisconnectReason.ServerRestart, true));
        }
    }

    /// <summary>Broadcastings that have subscribers, connections' internal channels included.</summary>
    public int StreamCount => Hub.StreamCount;

    public void Dispose()
    {
        lock (heartbeatGate)
        {
            heartbeat?.Dispose();
            heartbeat = null;
        }
    }

    /// <summary>
    /// The channel a client's <c>channel</c> names, with its class name: <c>safe_constantize</c>
    /// resolves "::RoomChannel" too, but the class (and every broadcasting it names) is "RoomChannel".
    /// </summary>
    internal (string ClassName, Func<Channel<TUser>> Factory)? FindChannel(string requested)
    {
        var name = requested.StartsWith("::", StringComparison.Ordinal) ? requested[2..] : requested;
        return channels.TryGetValue(name, out var factory) ? (name, factory) : null;
    }

    internal void Register(CableConnection<TUser> connection) => connections.TryAdd(connection, 0);

    internal void Unregister(CableConnection<TUser> connection) => connections.TryRemove(connection, out _);

    int BroadcastEncoded(string broadcasting, string payload)
    {
        CableLog.Broadcasting(Logger, broadcasting);
        return Hub.Broadcast(broadcasting, payload);
    }

    /// <summary>
    /// <c>Connection::Base#allow_request_origin?</c> with Campfire's configuration.
    /// </summary>
    bool AllowRequestOrigin(HttpRequest request)
    {
        if (Config.DisableRequestForgeryProtection)
        {
            return true;
        }
        var origin = request.Headers.Origin.FirstOrDefault();
        var host = request.Headers.Host.FirstOrDefault() ?? "";
        var scheme = Config.AssumeSsl || SslRequest(request) ? "https" : "http";
        if (Config.AllowSameOriginAsHost && origin == $"{scheme}://{host}")
        {
            return true;
        }
        if (origin is not null && Config.AllowedRequestOrigins.Contains(origin, StringComparer.Ordinal))
        {
            return true;
        }
        CableLog.OriginNotAllowed(Logger, origin);
        return false;
    }

    /// <summary><c>Rack::Request#ssl?</c>.</summary>
    static bool SslRequest(HttpRequest request)
    {
        if (request.IsHttps)
        {
            return true;
        }
        string? First(string name) => request.Headers[name].FirstOrDefault()?.Split(',')[0].Trim().ToLowerInvariant();
        return First("X-Forwarded-Ssl") == "on" || First("X-Forwarded-Scheme") == "https" || First("X-Forwarded-Proto") == "https";
    }

    /// <summary><c>Connection::Base#respond_to_invalid_request</c>.</summary>
    static async Task PageNotFoundAsync(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status404NotFound;
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync("Page not found").ConfigureAwait(false);
    }

    /// <summary>
    /// The server-wide heartbeat, started on the first request like Rails'
    /// <c>setup_heartbeat_timer</c>, so every connection pings in step and shares one frame per beat.
    /// </summary>
    void StartHeartbeat()
    {
        if (heartbeat is not null)
        {
            return;
        }
        lock (heartbeatGate)
        {
            heartbeat ??= Clock.CreateTimer(_ => Beat(), null, CableProtocol.BeatInterval, CableProtocol.BeatInterval);
        }
    }

    void Beat()
    {
        var ping = new Frame(CableProtocol.Ping(Clock.GetUtcNow().ToUnixTimeSeconds()));
        foreach (var connection in connections.Keys)
        {
            connection.Deliver(ping);
        }
    }
}

public static class CableServer
{
    public static CableServerBuilder<TUser> Builder<TUser>(CableConfig config, ICableAuthenticator<TUser> authenticator)
        where TUser : class => new(config, authenticator);

    /// <summary><c>ActionCable::Connection::InternalChannel#internal_channel</c>.</summary>
    public static string InternalChannel(string connectionIdentifier) => $"action_cable/{connectionIdentifier}";
}

/// <summary>Builds a <see cref="CableServer{TUser}"/>: its configuration, authenticator and channel classes.</summary>
public sealed class CableServerBuilder<TUser>
    where TUser : class
{
    readonly CableConfig config;
    readonly ICableAuthenticator<TUser> authenticator;
    readonly Dictionary<string, Func<Channel<TUser>>> channels = new(StringComparer.Ordinal);
    TimeProvider clock = TimeProvider.System;
    ILogger logger = NullLogger.Instance;

    internal CableServerBuilder(CableConfig config, ICableAuthenticator<TUser> authenticator)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(authenticator);
        this.config = config;
        this.authenticator = authenticator;
    }

    /// <summary>
    /// Registers a channel class under its Ruby class name (<c>"RoomChannel"</c>,
    /// <c>"Turbo::StreamsChannel"</c>), which is what clients put in the identifier's <c>channel</c>.
    /// </summary>
    public CableServerBuilder<TUser> Channel(string className, Func<Channel<TUser>> factory)
    {
        ArgumentNullException.ThrowIfNull(className);
        ArgumentNullException.ThrowIfNull(factory);
        channels[className] = factory;
        return this;
    }

    public CableServerBuilder<TUser> Clock(TimeProvider value)
    {
        clock = value;
        return this;
    }

    public CableServerBuilder<TUser> Logger(ILogger value)
    {
        logger = value;
        return this;
    }

    public CableServer<TUser> Build() => new(config, authenticator, new Dictionary<string, Func<Channel<TUser>>>(channels, StringComparer.Ordinal), clock, logger);
}
