using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Microsoft.AspNetCore.Http;

namespace Campfire.Cable.Tests.Server;

/// <summary>
/// Frame sequences recorded from the reference app's <c>/cable</c> (Thruster, Puma, the Redis
/// adapter), replayed against this server: reference-rust/crates/cable/tests/golden/reference.json,
/// scripted as reference-rust/crates/cable/tests/golden.rs scripts it. Every frame must match byte
/// for byte; only ping timestamps are normalized.
/// </summary>
/// <remarks>
/// Campfire's channels (reference/app/channels) are built here over the fixture values the
/// recording used: the real ones are RT02's (Turbo) and RT03's (app channels); these stand-ins do
/// only what the script exercises.
/// </remarks>
public sealed class GoldenReplayTests
{
    const string cookie = "session_token=replay";

    sealed record Exchange(string Step, List<string> Frames);

    abstract record Step;

    sealed record Connect(bool Cookie, bool OriginOk) : Step;

    sealed record HttpGet : Step;

    sealed record Send(string Text) : Step;

    sealed record AwaitPing : Step;

    /// <summary>Signing out on the reference; <see cref="CableServer{TUser}.Disconnect"/> here.</summary>
    sealed record RemoteDisconnect : Step;

    [Fact]
    public async Task Replays_the_reference_frames()
    {
        var golden = JsonNode.Parse(await File.ReadAllTextAsync(GoldenPath(), TestContext.Current.CancellationToken))!;
        var tokens = golden["tokens"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.Ordinal);

        await using var app = await CableTestApp.StartAsync(BuildServer(tokens));
        var actual = await RunScriptAsync(app, tokens);

        foreach (var (session, expectedNode) in golden["sessions"]!.AsObject())
        {
            var expected = expectedNode!.AsArray().Select(e => new Exchange((string)e!["step"]!, [.. e["frames"]!.AsArray().Select(f => (string)f!)])).ToList();
            Assert.Equal(expected.Count, actual[session].Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Step, actual[session][i].Step);
                Assert.True(
                    expected[i].Frames.SequenceEqual(actual[session][i].Frames),
                    $"session \"{session}\", step \"{expected[i].Step}\":\nexpected {JsonSerializer.Serialize(expected[i].Frames)}\nactual   {JsonSerializer.Serialize(actual[session][i].Frames)}");
            }
        }
    }

    static string GoldenPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "reference-rust", "crates", "cable", "tests", "golden", "reference.json");
            if (File.Exists(path))
            {
                return path;
            }
        }
        throw new FileNotFoundException("reference-rust/crates/cable/tests/golden/reference.json (git submodule update --init)");
    }

    static string Identifier(JsonObject value) => RailsJson.Generate(value);

    static string Subscribe(string identifier) => RailsJson.Generate(new JsonObject { ["command"] = "subscribe", ["identifier"] = identifier });

    static string Unsubscribe(string identifier) => RailsJson.Generate(new JsonObject { ["command"] = "unsubscribe", ["identifier"] = identifier });

    static string Perform(string identifier, JsonObject data) =>
        RailsJson.Generate(new JsonObject { ["command"] = "message", ["identifier"] = identifier, ["data"] = RailsJson.Generate(data) });

    static List<(string Session, List<(string Name, Step Step)> Steps)> Script(Dictionary<string, string> t)
    {
        var roomId = long.Parse(t["ROOM_ID"], System.Globalization.CultureInfo.InvariantCulture);
        var closedRoomId = long.Parse(t["CLOSED_ROOM_ID"], System.Globalization.CultureInfo.InvariantCulture);
        var heartbeat = Identifier(new JsonObject { ["channel"] = "HeartbeatChannel" });
        var room = Identifier(new JsonObject { ["channel"] = "RoomChannel", ["room_id"] = roomId });
        var closedRoom = Identifier(new JsonObject { ["channel"] = "RoomChannel", ["room_id"] = closedRoomId });
        var typing = Identifier(new JsonObject { ["channel"] = "TypingNotificationsChannel", ["room_id"] = roomId });
        string Turbo(string signed) => Identifier(new JsonObject { ["channel"] = "Turbo::StreamsChannel", ["signed_stream_name"] = signed });

        return
        [
            ("authenticated",
            [
                ("connect", new Connect(true, true)),
                ("subscribe heartbeat", new Send(Subscribe(heartbeat))),
                ("subscribe heartbeat again", new Send(Subscribe(heartbeat))),
                ("subscribe member room", new Send(Subscribe(room))),
                ("subscribe non-member room", new Send(Subscribe(closedRoom))),
                ("subscribe unknown channel", new Send(Subscribe(Identifier(new JsonObject { ["channel"] = "NopeChannel" })))),
                ("subscribe base channel", new Send(Subscribe(Identifier(new JsonObject { ["channel"] = "ApplicationCable::Channel" })))),
                ("subscribe turbo rooms", new Send(Subscribe(Turbo(t["ROOMS_SIGNED"])))),
                ("subscribe turbo forged", new Send(Subscribe(Turbo("InJvb21zIg==--0000")))),
                ("subscribe turbo unsigned", new Send(Subscribe(Identifier(new JsonObject { ["channel"] = "Turbo::StreamsChannel" })))),
                ("subscribe turbo guarded room messages", new Send(Subscribe(Turbo(t["ROOM_MESSAGES_SIGNED"])))),
                ("subscribe typing", new Send(Subscribe(typing))),
                ("perform typing start", new Send(Perform(typing, new JsonObject { ["action"] = "start" }))),
                ("perform unknown action", new Send(Perform(typing, new JsonObject { ["action"] = "dance" }))),
                ("perform default receive", new Send(Perform(typing, new JsonObject { ["text"] = "hi" }))),
                ("unsubscribe typing", new Send(Unsubscribe(typing))),
                ("perform after unsubscribe", new Send(Perform(typing, new JsonObject { ["action"] = "start" }))),
                ("unknown command", new Send(RailsJson.Generate(new JsonObject { ["command"] = "dance" }))),
                ("invalid json", new Send("not json")),
                ("ping", new AwaitPing()),
            ]),
            ("remote disconnect",
            [
                ("connect", new Connect(true, true)),
                ("subscribe member room", new Send(Subscribe(room))),
                ("sign out", new RemoteDisconnect()),
            ]),
            ("unauthenticated", [("connect", new Connect(false, true))]),
            ("cross origin", [("connect", new Connect(true, false))]),
            ("plain http", [("get", new HttpGet())]),
        ];
    }

    static async Task<Dictionary<string, List<Exchange>>> RunScriptAsync(CableTestApp<User> app, Dictionary<string, string> tokens)
    {
        var sessions = new Dictionary<string, List<Exchange>>(StringComparer.Ordinal);
        foreach (var (session, steps) in Script(tokens))
        {
            CableClient? client = null;
            var exchanges = new List<Exchange>();
            foreach (var (name, step) in steps)
            {
                List<string> frames;
                switch (step)
                {
                    case Connect { OriginOk: false } connect:
                        var refused = await app.UpgradeRequestAsync("http://evil.example", connect.Cookie ? cookie : null);
                        frames = [$"http {refused.Status} {refused.Body}"];
                        break;
                    case Connect connect:
                        client = await app.ConnectAsync(connect.Cookie ? cookie : null);
                        frames = [$"upgrade 101 protocol={client.Protocol}", .. await client.CollectAsync()];
                        break;
                    case HttpGet:
                        var (status, contentType, body) = await app.RawRequestAsync();
                        frames = [$"http {status} {contentType} {body}"];
                        break;
                    case Send send:
                        await client!.SendAsync(send.Text);
                        frames = await client.CollectAsync();
                        break;
                    case AwaitPing:
                        frames = await client!.CollectAsync(awaitPing: true);
                        break;
                    case RemoteDisconnect:
                        app.Server.Disconnect($"gid://campfire/User/{tokens["USER_ID"]}", reconnect: true);
                        frames = await client!.CollectAsync();
                        break;
                    default:
                        throw new InvalidOperationException(step.ToString());
                }
                exchanges.Add(new Exchange(name, frames));
            }
            client?.Dispose();
            sessions[session] = exchanges;
        }
        return sessions;
    }

    // Campfire's channels over the fixture values.

    sealed record User(long Id, string Name, long[] RoomIds);

    sealed class FixtureAuthenticator(Dictionary<string, string> tokens) : ICableAuthenticator<User>
    {
        public ValueTask<CableIdentity<User>?> ConnectAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            if (request.Headers.Cookie.ToString() != cookie)
            {
                return ValueTask.FromResult<CableIdentity<User>?>(null);
            }
            var id = long.Parse(tokens["USER_ID"], System.Globalization.CultureInfo.InvariantCulture);
            var user = new User(id, tokens["USER_NAME"], [long.Parse(tokens["ROOM_ID"], System.Globalization.CultureInfo.InvariantCulture)]);
            return ValueTask.FromResult<CableIdentity<User>?>(new(user, $"gid://campfire/User/{id}"));
        }
    }

    /// <summary><c>RoomChannel#subscribed</c>: <c>stream_for</c> a room the user is a member of, else reject.</summary>
    class RoomChannel : Channel<User>
    {
        protected string? Room { get; private set; }

        public override ValueTask SubscribedAsync()
        {
            var roomId = Params["room_id"] is JsonValue value && value.TryGetValue<long>(out var id) ? id : (long?)null;
            if (roomId is { } member && CurrentUser.RoomIds.Contains(member))
            {
                Room = $"room-{member}";
                StreamFor(Room);
            }
            else
            {
                Reject();
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary><c>TypingNotificationsChannel</c>: <c>start</c> and <c>stop</c> broadcast to the room.</summary>
    sealed class TypingNotificationsChannel : RoomChannel
    {
        public override ValueTask<bool> PerformAsync(string action, JsonObject data)
        {
            if (action is not ("start" or "stop"))
            {
                return ValueTask.FromResult(false);
            }
            BroadcastTo([Room!], new JsonObject
            {
                ["action"] = action,
                ["user"] = new JsonObject { ["id"] = CurrentUser.Id, ["name"] = CurrentUser.Name },
            });
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// <c>Turbo::StreamsChannel</c> with <c>RoomStreamsAreAuthorized</c>, verifying signed names by
    /// lookup in the recording's table (the real verifier is RT02's).
    /// </summary>
    sealed class TurboStreamsChannel(IReadOnlyDictionary<string, string> signed) : Channel<User>
    {
        public override ValueTask SubscribedAsync()
        {
            var name = Params["signed_stream_name"] is JsonValue value && value.TryGetValue<string>(out var s) && signed.TryGetValue(s, out var verified) ? verified : null;
            if (name is null || name.Split(':', 2) is [_, "messages"])
            {
                Reject();
            }
            else
            {
                StreamFrom(name);
            }
            return ValueTask.CompletedTask;
        }
    }

    static CableServer<User> BuildServer(Dictionary<string, string> tokens)
    {
        var roomGid = GlobalId.Create("Rooms::Open", tokens["ROOM_ID"]).ToParam();
        var signed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [tokens["ROOMS_SIGNED"]] = "rooms",
            [tokens["ROOM_MESSAGES_SIGNED"]] = $"{roomGid}:messages",
        };
        return CableServer.Builder(new CableConfig { AssumeSsl = false }, new FixtureAuthenticator(tokens))
            .Channel("ApplicationCable::Channel", () => new EmptyChannel<User>())
            .Channel("HeartbeatChannel", () => new EmptyChannel<User>())
            .Channel("RoomChannel", () => new RoomChannel())
            .Channel("TypingNotificationsChannel", () => new TypingNotificationsChannel())
            .Channel("Turbo::StreamsChannel", () => new TurboStreamsChannel(signed))
            .Build();
    }
}
