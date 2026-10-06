using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.RailsCompat.Crypto;
using Microsoft.AspNetCore.Http;

namespace Campfire.Cable.Tests.Server;

/// <summary>
/// The Action Cable protocol, driven over a real socket: reference-rust/crates/cable/tests/protocol.rs.
/// </summary>
public sealed class ProtocolTests
{
    const string welcome = """{"type":"welcome"}""";

    static string Identifier(JsonObject value) => RailsJson.Generate(value);

    static string Room(long id) => Identifier(new JsonObject { ["channel"] = "RoomChannel", ["room_id"] = id });

    static string Confirm(string identifier) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"type":"confirm_subscription"}""";

    static string Reject(string identifier) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"type":"reject_subscription"}""";

    static string Message(string identifier, string message) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"message":{{message}}}""";

    [Fact]
    public async Task Non_websocket_requests_get_rails_404()
    {
        await using var app = await StartAsync();
        Assert.Equal((404, "text/plain; charset=utf-8", "Page not found"), await app.RawRequestAsync());
    }

    [Fact]
    public async Task Cross_origin_upgrades_get_rails_404()
    {
        await using var app = await StartAsync();
        var (status, _, body) = await app.UpgradeRequestAsync("http://evil.example", "session_token=1");
        Assert.Equal((404, "Page not found"), (status, body));
        Assert.Contains(app.Log.Entries, entry => entry.Contains("Request origin not allowed: http://evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_origin_under_assume_ssl_means_https()
    {
        await using var app = await StartAsync(new CableConfig());
        var (status, _, _) = await app.UpgradeRequestAsync(app.Origin, "session_token=1");
        Assert.Equal(404, status);

        using var client = await app.ConnectAsync("session_token=1", app.Origin.Replace("http://", "https://", StringComparison.Ordinal));
        Assert.Equal(welcome, await client.NextAsync());
    }

    [Fact]
    public async Task Allowed_request_origins_are_accepted_as_written()
    {
        await using var app = await StartAsync(TestConfig with { AllowedRequestOrigins = ["https://chat.example"] });
        using var client = await app.ConnectAsync("session_token=1", "https://chat.example");
        Assert.Equal(welcome, await client.NextAsync());
    }

    [Fact]
    public async Task Negotiates_the_actioncable_subprotocol_and_welcomes()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        Assert.Equal("actioncable-v1-json", client.Protocol);
        Assert.Equal(welcome, await client.NextAsync());
    }

    [Fact]
    public void Negotiation_follows_the_clients_order()
    {
        Assert.Equal("actioncable-v1-json", CableProtocol.NegotiateProtocol(["actioncable-v1-json, actioncable-unsupported"]));
        Assert.Equal("actioncable-unsupported", CableProtocol.NegotiateProtocol(["actioncable-unsupported, actioncable-v1-json"]));
        Assert.Null(CableProtocol.NegotiateProtocol(["foo"]));
        Assert.Null(CableProtocol.NegotiateProtocol([]));
    }

    [Fact]
    public async Task Unauthorized_connections_are_told_not_to_reconnect_and_closed()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync(cookie: null);
        Assert.Equal("""{"type":"disconnect","reason":"unauthorized","reconnect":false}""", await client.NextAsync());
        Assert.Equal("close Some((1000, \"\"))", await client.NextAsync());
        Assert.Equal(0, app.Server.StreamCount);
    }

    [Fact]
    public async Task Subscribe_confirms_and_duplicates_are_ignored()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        Assert.Equal(welcome, await client.NextAsync());

        var room = Room(1);
        await client.SubscribeAsync(room);
        Assert.Equal(Confirm(room), await client.NextAsync());

        // Same identifier string: no reply at all.
        await client.SubscribeAsync(room);
        var heartbeat = Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" });
        await client.SubscribeAsync(heartbeat);
        Assert.Equal(Confirm(heartbeat), await client.NextAsync());

        // A differently spelled identifier for the same room is a separate subscription.
        const string respelled = """{"room_id":1,"channel":"RoomChannel"}""";
        await client.SubscribeAsync(respelled);
        Assert.Equal(Confirm(respelled), await client.NextAsync());
    }

    [Fact]
    public async Task Rejection_runs_unsubscribed_and_can_be_retried()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();

        var room = Room(2);
        await client.SubscribeAsync(room);
        Assert.Equal(Reject(room), await client.NextAsync());
        Assert.Equal(["subscribed rejected=True", "unsubscribed rejected=True"], app.Channels);

        // The rejected subscription was removed, so subscribing again is answered again.
        await client.SubscribeAsync(room);
        Assert.Equal(Reject(room), await client.NextAsync());
    }

    [Fact]
    public async Task Unknown_channels_and_malformed_commands_get_no_reply()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();

        await client.SubscribeAsync(Identifier(new JsonObject { ["channel"] = "NopeChannel" }));
        await client.SubscribeAsync("not json");
        await client.SendAsync("not json");
        await client.SendAsync(new JsonObject { ["command"] = "dance" });
        await client.UnsubscribeAsync(Room(1));
        await client.PerformAsync(Room(1), new JsonObject { ["action"] = "echo" });
        await client.AssertSilentAsync();

        // The connection is still usable afterwards.
        var room = Room(1);
        await client.SubscribeAsync(room);
        Assert.Equal(Confirm(room), await client.NextAsync());
    }

    [Fact]
    public async Task A_raising_subscribed_is_neither_confirmed_nor_rejected()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        await client.SubscribeAsync(Identifier(new JsonObject { ["channel"] = "RaisingChannel" }));
        await client.AssertSilentAsync();
        Assert.Contains(app.Log.Entries, entry => entry.StartsWith("Error: Could not execute command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leading_colons_resolve_like_safe_constantize()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        var heartbeat = Identifier(new JsonObject { ["channel"] = "::HeartbeatChannel" });
        await client.SubscribeAsync(heartbeat);
        Assert.Equal(Confirm(heartbeat), await client.NextAsync());
    }

    /// <summary>A channel's broadcastings are named after its class, not after how the client spelled it.</summary>
    [Fact]
    public async Task Leading_colons_stream_and_broadcast_under_the_class_name()
    {
        await using var app = await StartAsync();
        using var prefixed = await app.ConnectAsync("session_token=1");
        using var plain = await app.ConnectAsync("session_token=1");
        await prefixed.NextAsync();
        await plain.NextAsync();
        var prefixedRoom = Identifier(new JsonObject { ["channel"] = "::RoomChannel", ["room_id"] = 1 });
        await prefixed.SubscribeAsync(prefixedRoom);
        Assert.Equal(Confirm(prefixedRoom), await prefixed.NextAsync());
        var plainRoom = Room(1);
        await plain.SubscribeAsync(plainRoom);
        Assert.Equal(Confirm(plainRoom), await plain.NextAsync());

        Assert.Equal(2, app.Server.BroadcastTo("RoomChannel", ["room-1"], new JsonObject { ["roomId"] = 1 }));
        Assert.Equal(Message(prefixedRoom, """{"roomId":1}"""), await prefixed.NextAsync());
        Assert.Equal(Message(plainRoom, """{"roomId":1}"""), await plain.NextAsync());

        await prefixed.PerformAsync(prefixedRoom, new JsonObject { ["action"] = "start" });
        const string start = """{"action":"start","user":{"id":1}}""";
        Assert.Equal(Message(plainRoom, start), await plain.NextAsync());
        Assert.Equal(Message(prefixedRoom, start), await prefixed.NextAsync());
    }

    [Fact]
    public async Task Broadcasts_reach_subscribers_as_escaped_json()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        var room = Room(1);
        await client.SubscribeAsync(room);
        await client.NextAsync();

        Assert.Equal(1, app.Server.BroadcastTo("RoomChannel", ["room-1"], new JsonObject { ["roomId"] = 1 }));
        Assert.Equal(Message(room, """{"roomId":1}"""), await client.NextAsync());

        app.Server.Broadcast("room:room-1", JsonValue.Create("<b>&</b>"));
        Assert.Equal(Message(room, "\"\\u003cb\\u003e\\u0026\\u003c/b\\u003e\""), await client.NextAsync());

        // IBroadcaster: the payload is already encoded.
        app.Server.Broadcast("room:room-1", "[1,2]");
        Assert.Equal(Message(room, "[1,2]"), await client.NextAsync());
    }

    [Fact]
    public async Task Perform_dispatches_actions_and_defaults_to_receive()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        using var other = await app.ConnectAsync("session_token=2");
        await client.NextAsync();
        await other.NextAsync();
        var room = Room(1);
        await client.SubscribeAsync(room);
        await other.SubscribeAsync(room);
        await client.NextAsync();
        await other.NextAsync();

        await client.PerformAsync(room, new JsonObject { ["action"] = "echo", ["text"] = "hi" });
        Assert.Equal(Message(room, """{"action":"echo","text":"hi"}"""), await client.NextAsync());

        await client.PerformAsync(room, new JsonObject { ["text"] = "hi" });
        Assert.Equal(Message(room, """{"received":{"text":"hi"}}"""), await client.NextAsync());

        await client.PerformAsync(room, new JsonObject { ["action"] = " ", ["text"] = "blank" });
        Assert.Equal(Message(room, """{"received":{"action":" ","text":"blank"}}"""), await client.NextAsync());

        await client.PerformAsync(room, new JsonObject { ["action"] = "start" });
        var typing = Message(room, """{"action":"start","user":{"id":1}}""");
        Assert.Equal(typing, await client.NextAsync());
        Assert.Equal(typing, await other.NextAsync());

        await client.PerformAsync(room, new JsonObject { ["action"] = "not_an_action" });
        await client.AssertSilentAsync();
        Assert.Contains("Error: Unable to process RoomChannel#not_an_action", app.Log.Entries);
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery_silently()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        var room = Room(1);
        await client.SubscribeAsync(room);
        await client.NextAsync();

        await client.UnsubscribeAsync(room);
        await client.AssertSilentAsync();
        Assert.Equal(0, app.Server.Broadcast("room:room-1", new JsonObject()));
        await client.AssertSilentAsync();
        Assert.Equal(1, app.Server.StreamCount); // only the internal channel remains
    }

    [Fact]
    public async Task Remote_disconnect_closes_every_connection_for_the_identifier()
    {
        await using var app = await StartAsync();
        using var first = await app.ConnectAsync("session_token=1");
        using var second = await app.ConnectAsync("session_token=1");
        using var bystander = await app.ConnectAsync("session_token=2");
        foreach (var client in new[] { first, second, bystander })
        {
            await client.NextAsync();
        }
        var room = Room(1);
        await first.SubscribeAsync(room);
        await first.NextAsync();

        Assert.Equal(2, app.Server.Disconnect("user-1", reconnect: true));
        foreach (var client in new[] { first, second })
        {
            Assert.Equal("""{"type":"disconnect","reason":"remote","reconnect":true}""", await client.NextAsync());
            Assert.Equal("close Some((1000, \"\"))", await client.NextAsync());
        }
        await bystander.AssertSilentAsync();

        // Once the client completes the close handshake, the server unsubscribes its channels.
        Assert.Equal("end", await first.NextAsync());
        await Eventually(() => app.Channels.LastOrDefault() == "unsubscribed rejected=False");
        await Eventually(() => app.Server.ConnectionCount == 1);

        using var again = await app.ConnectAsync("session_token=1");
        await again.NextAsync();
        app.Server.Disconnect("user-1", reconnect: false);
        Assert.Equal("""{"type":"disconnect","reason":"remote","reconnect":false}""", await again.NextAsync());
    }

    [Fact]
    public async Task Restart_closes_with_server_restart()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        app.Server.Restart();
        Assert.Equal("""{"type":"disconnect","reason":"server_restart","reconnect":true}""", await client.NextAsync());
    }

    [Fact]
    public async Task Lagging_subscribers_are_disconnected_with_reconnect()
    {
        await using var app = await StartAsync(TestConfig with { QueueCapacity = 1 });
        using var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        var room = Room(1);
        await client.SubscribeAsync(room);
        await client.NextAsync();

        for (var i = 0; i < 10_000; i++)
        {
            app.Server.Broadcast("room:room-1", JsonValue.Create(i));
        }
        var delivered = new List<string>();
        while (true)
        {
            var frame = await client.NextAsync();
            if (frame.Contains("\"type\":\"disconnect\"", StringComparison.Ordinal))
            {
                Assert.Equal("""{"type":"disconnect","reason":null,"reconnect":true}""", frame);
                break;
            }
            delivered.Add(frame);
        }
        Assert.True(delivered.Count < 10_000);
        // What did arrive came in order, with no gaps before the disconnect.
        Assert.Equal(Enumerable.Range(0, delivered.Count).Select(i => Message(room, i.ToString(System.Globalization.CultureInfo.InvariantCulture))), delivered);
    }

    [Fact]
    public async Task Pings_every_three_seconds_with_a_unix_timestamp()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        Assert.Equal(welcome, await client.NextAsync());
        var started = DateTime.UtcNow;
        var ping = JsonNode.Parse(await client.NextAsync(pings: true))!.AsObject();
        Assert.True(DateTime.UtcNow - started <= TimeSpan.FromMilliseconds(3100));
        Assert.Equal(["type", "message"], ping.Select(p => p.Key));
        Assert.Equal("ping", (string)ping["type"]!);
        Assert.InRange((long)ping["message"]! - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), -1, 1);
    }

    [Fact]
    public async Task A_client_close_is_answered_with_its_code()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        Assert.Equal(welcome, await client.NextAsync());
        await client.Socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, TestContext.Current.CancellationToken);
        Assert.Equal("close Some((1001, \"\"))", await client.NextAsync());
    }

    [Fact]
    public async Task A_message_over_the_limit_closes_with_1009()
    {
        await using var app = await StartAsync(TestConfig with { MaxMessageBytes = 1024 });
        using var client = await app.ConnectAsync("session_token=1");
        Assert.Equal(welcome, await client.NextAsync());
        await client.SendAsync(new string('x', 2048));
        Assert.Equal("close Some((1009, \"\"))", await client.NextAsync());
    }

    [Fact]
    public async Task A_connection_holds_a_bounded_number_of_subscriptions()
    {
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync("session_token=1");
        Assert.Equal(welcome, await client.NextAsync());
        for (var nonce = 0; nonce < 64; nonce++)
        {
            var heartbeat = Identifier(new JsonObject { ["channel"] = "HeartbeatChannel", ["nonce"] = nonce });
            await client.SubscribeAsync(heartbeat);
            Assert.Equal(Confirm(heartbeat), await client.NextAsync());
        }
        // Past the limit, and an oversized identifier: ignored, no reply.
        await client.SubscribeAsync(Identifier(new JsonObject { ["channel"] = "HeartbeatChannel", ["nonce"] = 64 }));
        await client.AssertSilentAsync();
        using var other = await app.ConnectAsync("session_token=2");
        Assert.Equal(welcome, await other.NextAsync());
        await other.SubscribeAsync(Identifier(new JsonObject { ["channel"] = "HeartbeatChannel", ["pad"] = new string('x', 5000) }));
        await other.AssertSilentAsync();
    }

    [Fact]
    public async Task Closing_unsubscribes_every_channel_and_releases_its_streams()
    {
        await using var app = await StartAsync();
        var client = await app.ConnectAsync("session_token=1");
        await client.NextAsync();
        await client.SubscribeAsync(Room(1));
        await client.NextAsync();
        Assert.Equal(2, app.Server.StreamCount);

        client.Dispose();
        await Eventually(() => app.Server.StreamCount == 0 && app.Server.ConnectionCount == 0);
        Assert.Equal("unsubscribed rejected=False", app.Channels.Last());
    }

    [Fact]
    public async Task Permessage_deflate_is_negotiated_and_frames_read_the_same()
    {
        await using var app = await StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("actioncable-v1-json");
        socket.Options.SetRequestHeader("Origin", app.Origin);
        socket.Options.SetRequestHeader("Cookie", "session_token=1");
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        socket.Options.CollectHttpResponseDetails = true;
        await socket.ConnectAsync(app.Url, TestContext.Current.CancellationToken);
        Assert.StartsWith("permessage-deflate", socket.HttpResponseHeaders!["Sec-WebSocket-Extensions"].Single(), StringComparison.Ordinal);
        using var client = new CableClient(socket);
        Assert.Equal(welcome, await client.NextAsync());

        var room = Room(1);
        await client.SubscribeAsync(room);
        Assert.Equal(Confirm(room), await client.NextAsync());
        var text = new string('a', 4000);
        app.Server.Broadcast("room:room-1", JsonValue.Create(text));
        Assert.Equal(Message(room, $"\"{text}\""), await client.NextAsync());
    }

    [Fact]
    public async Task Compression_can_be_turned_off()
    {
        await using var app = await StartAsync(TestConfig with { EnableCompression = false });
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", app.Origin);
        socket.Options.SetRequestHeader("Cookie", "session_token=1");
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        socket.Options.CollectHttpResponseDetails = true;
        await socket.ConnectAsync(app.Url, TestContext.Current.CancellationToken);
        Assert.False(socket.HttpResponseHeaders!.ContainsKey("Sec-WebSocket-Extensions"));
        // No subprotocol offered, none chosen; the server still speaks the JSON protocol.
        Assert.Null(socket.SubProtocol);
        using var client = new CableClient(socket);
        Assert.Equal(welcome, await client.NextAsync());
    }

    // A tiny app on top of the cable server.

    static readonly CableConfig TestConfig = new() { AssumeSsl = false };

    static async Task<App> StartAsync(CableConfig? config = null)
    {
        var log = new TestLogger();
        var channels = new ConcurrentQueue<string>();
        var server = CableServer.Builder(config ?? TestConfig, new CookieAuthenticator())
            .Logger(log)
            .Channel("HeartbeatChannel", () => new EmptyChannel<User>())
            .Channel("RoomChannel", () => new RoomChannel(channels))
            .Channel("RaisingChannel", () => new RaisingChannel())
            .Build();
        return new App(await CableTestApp.StartAsync(server), log, channels);
    }

    sealed class App(CableTestApp<User> inner, TestLogger log, ConcurrentQueue<string> channels) : IAsyncDisposable
    {
        public CableServer<User> Server => inner.Server;

        public string Origin => inner.Origin;

        public Uri Url => inner.Url;

        public TestLogger Log => log;

        /// <summary>RoomChannel's callbacks, in the order they ran.</summary>
        public List<string> Channels => [.. channels];

        public Task<CableClient> ConnectAsync(string? cookie, string? origin = null) => inner.ConnectAsync(cookie, origin);

        public Task<(int Status, string ContentType, string Body)> RawRequestAsync(params string[] headers) => inner.RawRequestAsync(headers);

        public Task<(int Status, string ContentType, string Body)> UpgradeRequestAsync(string origin, string? cookie) => inner.UpgradeRequestAsync(origin, cookie);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    sealed record User(long Id, long[] RoomIds);

    /// <summary><c>Cookie: session_token=&lt;user id&gt;</c>; users 1 and 2 exist, both members of room 1 only.</summary>
    sealed class CookieAuthenticator : ICableAuthenticator<User>
    {
        public ValueTask<CableIdentity<User>?> ConnectAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            var cookie = request.Headers.Cookie.ToString();
            CableIdentity<User>? identity = cookie switch
            {
                "session_token=1" => new(new User(1, [1]), "user-1"),
                "session_token=2" => new(new User(2, [1]), "user-2"),
                _ => null,
            };
            return ValueTask.FromResult(identity);
        }
    }

    /// <summary>Like <c>RoomChannel</c> plus <c>TypingNotificationsChannel</c>'s actions, and an echo.</summary>
    sealed class RoomChannel(ConcurrentQueue<string> log) : Channel<User>
    {
        string? room;

        public override ValueTask SubscribedAsync()
        {
            var roomId = Params["room_id"] is JsonValue value && value.TryGetValue<long>(out var id) ? id : (long?)null;
            if (roomId is { } member && CurrentUser.RoomIds.Contains(member))
            {
                room = $"room-{member}";
                StreamFor(room);
            }
            else
            {
                Reject();
            }
            log.Enqueue($"subscribed rejected={SubscriptionRejected}");
            return ValueTask.CompletedTask;
        }

        public override ValueTask UnsubscribedAsync()
        {
            log.Enqueue($"unsubscribed rejected={SubscriptionRejected}");
            return ValueTask.CompletedTask;
        }

        public override ValueTask<bool> PerformAsync(string action, JsonObject data)
        {
            switch (action)
            {
                case "start":
                    BroadcastTo([room!], new JsonObject { ["action"] = "start", ["user"] = new JsonObject { ["id"] = CurrentUser.Id } });
                    return ValueTask.FromResult(true);
                case "echo":
                    Transmit(data.DeepClone());
                    return ValueTask.FromResult(true);
                case "receive":
                    Transmit(new JsonObject { ["received"] = data.DeepClone() });
                    return ValueTask.FromResult(true);
                default:
                    return ValueTask.FromResult(false);
            }
        }
    }

    sealed class RaisingChannel : Channel<User>
    {
        public override ValueTask SubscribedAsync() => throw new InvalidOperationException("boom");
    }
}
