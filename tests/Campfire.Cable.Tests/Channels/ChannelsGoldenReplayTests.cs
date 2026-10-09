using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Cable.Channels;
using Campfire.Cable.Server;
using Campfire.Cable.Tests.Server;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;
using Microsoft.Data.Sqlite;

namespace Campfire.Cable.Tests.Channels;

/// <summary>
/// Replays reference-rust <c>channels/tests/golden/reference.json</c>: the frames the reference
/// recorded for every Campfire channel, compared per socket as a sorted list. Pings are dropped.
/// </summary>
public class ChannelsGoldenReplayTests
{
    [Fact]
    public async Task Cable_replay_for_every_channel_equals_the_reference()
    {
        var recording = JsonNode.Parse(File.ReadAllText(GoldenPath()))!.AsObject();
        var fixtures = recording["fixtures"]!.AsObject();
        var cookies = Strings(fixtures["cookies"]!.AsObject());
        var tokens = Strings(fixtures["tokens"]!.AsObject());
        var expected = recording["steps"]!.AsArray().Select(ReadStep).ToList();

        var directory = Path.Combine(Path.GetTempPath(), "campfire-cable-golden", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(directory, "production.sqlite3")) { Readers = 2 });
        CableTestApp<User>? app = null;
        var sockets = new Dictionary<string, CableClient>(StringComparer.Ordinal);
        try
        {
            await database.WriteAsync(tx => InsertFixtures(tx.Session, fixtures["rows"]!.AsObject()), TestContext.Current.CancellationToken);
            var keys = new KeyGenerator(RailsCompatVectors.File.SecretKeyBase);
            var server = AppChannels.Register(
                CableServer.Builder(new CableConfig { AssumeSsl = false }, new SessionCookieAuthenticator(database, keys)),
                database,
                keys).Build();
            var seams = new DomainSeams(new NullBroadcaster(), new NullJobs(), new ServerRevoker(server));
            app = await CableTestApp.StartAsync(server);

            var script = Script(tokens);
            Assert.Equal(script.Select(step => step.Name), expected.Select(step => step.Name));
            var replay = new Replay(app, database, seams, cookies, sockets);
            for (var i = 0; i < script.Count; i++)
            {
                await script[i].Run(replay, tokens);
                var actual = await CollectAsync(sockets, expected[i].Frames);
                AssertSame(script[i].Name, expected[i].Frames, actual);
            }
        }
        finally
        {
            foreach (var socket in sockets.Values)
            {
                socket.Dispose();
            }

            if (app is not null)
            {
                await app.DisposeAsync();
            }

            database.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    static List<ScriptStep> Script(Dictionary<string, string> tokens)
    {
        var roomId = long.Parse(tokens["ROOM_ID"], CultureInfo.InvariantCulture);
        var closedId = long.Parse(tokens["CLOSED_ID"], CultureInfo.InvariantCulture);
        var reads = Id(new JsonObject { ["channel"] = "ReadRoomsChannel" });
        var unreads = Id(new JsonObject { ["channel"] = "UnreadRoomsChannel" });
        var presence = Room("PresenceChannel", roomId);
        var presenceClosed = Room("PresenceChannel", closedId);
        var presenceText = Room("PresenceChannel", "abc");
        var presenceString = Room("PresenceChannel", roomId.ToString(CultureInfo.InvariantCulture));
        var room = Room("RoomChannel", roomId);
        var roomClosed = Room("RoomChannel", closedId);
        var roomMissing = Id(new JsonObject { ["channel"] = "RoomChannel" });
        var typing = Room("TypingNotificationsChannel", roomId);
        var messages = MessageChannel(tokens["ROOM_MESSAGES_SIGNED"]);
        var messagesClosed = MessageChannel(tokens["CLOSED_MESSAGES_SIGNED"]);
        var messagesMissing = Id(new JsonObject { ["channel"] = "RoomMessagesChannel" });
        const string forged = "InJvb21zIg==--0000";
        var messagesForged = MessageChannel(forged);
        var messagesRooms = MessageChannel(tokens["ROOMS_SIGNED"]);
        var turboRooms = Turbo(tokens["ROOMS_SIGNED"]);
        var turboOwn = Turbo(tokens["A_ROOMS_SIGNED"]);
        var turboGuarded = Turbo(tokens["ROOM_MESSAGES_SIGNED"]);
        var turboForged = Turbo(forged);

        return
        [
            Connect("connect A", "A", "A"),
            Connect("connect B", "B", "B"),
            Connect("connect without a cookie", "X", null),
            Subscribe("A heartbeat", "A", Id(new JsonObject { ["channel"] = "HeartbeatChannel" })),
            Subscribe("A base channel", "A", Id(new JsonObject { ["channel"] = "ApplicationCable::Channel" })),
            Subscribe("A reads", "A", reads),
            Subscribe("A unreads", "A", unreads),
            Subscribe("A presence", "A", presence),
            Subscribe("A presence in a room A isn't in", "A", presenceClosed),
            Subscribe("A presence with a non-numeric room", "A", presenceText),
            Subscribe("A presence with a numeric string room", "A", presenceString),
            Subscribe("A room", "A", room),
            Subscribe("A room A isn't in", "A", roomClosed),
            Subscribe("A room without an id", "A", roomMissing),
            Subscribe("A typing", "A", typing),
            Subscribe("B typing", "B", typing),
            Perform("A starts typing", "A", typing, new JsonObject { ["action"] = "start" }),
            Perform("B stops typing", "B", typing, new JsonObject { ["action"] = "stop", ["extra"] = 1 }),
            Perform("A performs an unknown typing action", "A", typing, new JsonObject { ["action"] = "dance" }),
            Subscribe("A room messages", "A", messages),
            Subscribe("A room messages for a room A isn't in", "A", messagesClosed),
            Subscribe("A room messages without a name", "A", messagesMissing),
            Subscribe("A room messages with a forged name", "A", messagesForged),
            Subscribe("A room messages with the rooms name", "A", messagesRooms),
            Subscribe("A turbo rooms", "A", turboRooms),
            Subscribe("A turbo own rooms", "A", turboOwn),
            Subscribe("A turbo guarded room messages", "A", turboGuarded),
            Subscribe("A turbo forged", "A", turboForged),
            Perform("A presence present", "A", presence, new JsonObject { ["action"] = "present" }),
            Perform("A presence refresh", "A", presence, new JsonObject { ["action"] = "refresh" }),
            Perform("A unreads subscribed again", "A", unreads, new JsonObject { ["action"] = "subscribed" }),
            Trigger("unread fanout", "unread", "MESSAGE_ID"),
            Trigger("message removed", "remove_message", "MESSAGE_ID"),
            Subscribe("B presence", "B", presence),
            Perform("A typing again", "A", typing, new JsonObject { ["action"] = "start" }),
            Trigger("revoke A", "revoke", "ROOM_ID", "A_ID"),
            Connect("reconnect A", "A2", "A"),
            Subscribe("A2 presence", "A2", presence),
            Subscribe("A2 room", "A2", room),
            Subscribe("A2 typing", "A2", typing),
            Subscribe("A2 room messages", "A2", messages),
            Subscribe("A2 turbo guarded room messages", "A2", turboGuarded),
            Subscribe("A2 turbo rooms", "A2", turboRooms),
            Subscribe("A2 unreads", "A2", unreads),
            Perform("B typing after A left", "B", typing, new JsonObject { ["action"] = "start" }),
            Trigger("deactivate B", "deactivate", "B_ID"),
            Connect("reconnect B", "B2", "B"),
        ];

        static ScriptStep Connect(string name, string socket, string? cookie) => new(name, (replay, _) => replay.ConnectAsync(socket, cookie));
        static ScriptStep Subscribe(string name, string socket, string identifier) => new(name, (replay, _) => replay.Sockets[socket].SubscribeAsync(identifier));
        static ScriptStep Perform(string name, string socket, string identifier, JsonObject data) => new(name, (replay, _) => replay.Sockets[socket].PerformAsync(identifier, data));
        static ScriptStep Trigger(string name, string eventName, params string[] args) => new(name, (replay, names) => replay.TriggerAsync(eventName, args.Select(arg => long.Parse(names[arg], CultureInfo.InvariantCulture)).ToArray()));
    }

    static string Id(JsonObject identifier) => RailsJson.Generate(identifier);

    static string Room(string channel, long roomId) => Id(new JsonObject { ["channel"] = channel, ["room_id"] = roomId });

    static string Room(string channel, string roomId) => Id(new JsonObject { ["channel"] = channel, ["room_id"] = roomId });

    static string MessageChannel(string signed) => Id(new JsonObject { ["channel"] = "RoomMessagesChannel", ["signed_stream_name"] = signed });

    static string Turbo(string signed) => Id(new JsonObject { ["channel"] = "Turbo::StreamsChannel", ["signed_stream_name"] = signed });

    static async Task<Dictionary<string, List<string>>> CollectAsync(Dictionary<string, CableClient> sockets, Dictionary<string, List<string>> expected)
    {
        var actual = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await Task.WhenAll(sockets.Select(async pair =>
        {
            var atLeast = expected.TryGetValue(pair.Key, out var frames) ? frames.Count : 0;
            var received = await TakeAsync(pair.Value, atLeast);
            if (received.Count > 0)
            {
                lock (actual)
                {
                    actual[pair.Key] = received;
                }
            }
        }));
        return actual;
    }

    static async Task<List<string>> TakeAsync(CableClient client, int atLeast)
    {
        var frames = new List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var batch = await client.CollectAsync();
            foreach (var frame in batch)
            {
                if (!string.Equals(frame, "end", StringComparison.Ordinal))
                {
                    frames.Add(frame);
                }
            }

            if (atLeast == 0 || frames.Count >= atLeast || DateTime.UtcNow >= deadline)
            {
                break;
            }
        }

        frames.Sort(StringComparer.Ordinal);
        return frames;
    }

    static void AssertSame(string step, Dictionary<string, List<string>> expected, Dictionary<string, List<string>> actual)
    {
        var expectedText = Format(expected);
        var actualText = Format(actual);
        Assert.True(expectedText == actualText, $"{step}\nexpected:\n{expectedText}\nactual:\n{actualText}");
    }

    static string Format(Dictionary<string, List<string>> frames) =>
        string.Join("\n", frames.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ": " + string.Join(" | ", pair.Value)));

    static Exchange ReadStep(JsonNode? node)
    {
        var step = node!.AsObject();
        var frames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (socket, list) in step["frames"]!.AsObject())
        {
            var received = list!.AsArray().Select(frame => frame!.GetValue<string>()).ToList();
            received.Sort(StringComparer.Ordinal);
            frames[socket] = received;
        }

        return new Exchange(step["step"]!.GetValue<string>(), frames);
    }

    static Dictionary<string, string> Strings(JsonObject obj) =>
        obj.ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>());

    static void InsertFixtures(SqliteSession session, JsonObject rows)
    {
        foreach (var table in new[] { "users", "rooms", "memberships", "sessions", "messages" })
        {
            foreach (var row in rows[table]!.AsArray())
            {
                var columns = new List<string>();
                var names = new List<string>();
                var parameters = new List<(string Name, object? Value)>();
                var index = 0;
                foreach (var (column, value) in row!.AsObject())
                {
                    foreach (var character in column)
                    {
                        var letter = character is >= 'a' and <= 'z';
                        if (!letter && character != '_')
                        {
                            throw new InvalidDataException(column);
                        }
                    }

                    var name = "@p" + index.ToString(CultureInfo.InvariantCulture);
                    columns.Add("\"" + column + "\"");
                    names.Add(name);
                    parameters.Add((name, SqlValue(value)));
                    index++;
                }

                session.Execute(
                    $"INSERT INTO \"{table}\" ({string.Join(", ", columns)}) VALUES ({string.Join(", ", names)})",
                    parameters.ToArray());
            }
        }
    }

    static object? SqlValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return node?.ToJsonString();
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Null => null,
            JsonValueKind.Number => Number(value),
            _ => value.ToJsonString(),
        };
    }

    static object Number(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.TryGetInt64(out var whole) ? whole : element.GetDouble();
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<int>(out var narrow))
        {
            return narrow;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return real;
        }

        return value.ToJsonString();
    }

    static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "reference-rust", "crates", "campfire", "src", "channels", "tests", "golden", "reference.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("channels golden reference.json");
    }

    sealed record Exchange(string Name, Dictionary<string, List<string>> Frames);

    sealed record ScriptStep(string Name, Func<Replay, Dictionary<string, string>, Task> Run);

    sealed class Replay(CableTestApp<User> app, SqliteDatabase database, DomainSeams seams, Dictionary<string, string> cookies, Dictionary<string, CableClient> sockets)
    {
        public Dictionary<string, CableClient> Sockets => sockets;

        public async Task ConnectAsync(string name, string? cookieKey)
        {
            sockets[name] = await app.ConnectAsync(cookieKey is null ? null : cookies[cookieKey]);
        }

        public async Task TriggerAsync(string eventName, long[] args)
        {
            switch (eventName)
            {
                case "unread":
                    await database.ReadAsync(session =>
                    {
                        var message = Messages.Find(session, args[0]) ?? throw new InvalidDataException("message");
                        var room = Rooms.Find(session, message.RoomId) ?? throw new InvalidDataException("room");
                        UnreadRoomsChannel.BroadcastUnread(app.Server, session, room.Id);
                        return true;
                    }, TestContext.Current.CancellationToken);
                    break;
                case "remove_message":
                    await database.ReadAsync(session =>
                    {
                        var message = Messages.Find(session, args[0]) ?? throw new InvalidDataException("message");
                        var room = Rooms.Find(session, message.RoomId) ?? throw new InvalidDataException("room");
                        var html = $"<turbo-stream action=\"remove\" target=\"message_{message.ClientMessageId}\"></turbo-stream>";
                        app.Server.Broadcast($"{RoomSubscription.GidParam(room)}:messages", RailsJson.Encode(JsonValue.Create(html)));
                        return true;
                    }, TestContext.Current.CancellationToken);
                    break;
                case "revoke":
                    await MembershipLifecycle.RevokeFromAsync(database, seams, args[0], [args[1]]);
                    break;
                case "deactivate":
                    await database.WriteAsync(tx =>
                    {
                        var user = Users.Find(tx.Session, args[0]) ?? throw new InvalidDataException("user");
                        UserLifecycle.Deactivate(tx, seams, user, DateTimeOffset.UtcNow);
                    }, TestContext.Current.CancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(eventName);
            }
        }
    }

    sealed class NullJobs : IJobQueue
    {
        public void Enqueue(Job job) => ArgumentNullException.ThrowIfNull(job);
    }

    sealed class NullBroadcaster : IBroadcaster
    {
        public void Broadcast(string stream, string payload)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(payload);
        }
    }

    sealed class ServerRevoker(CableServer<User> server) : IConnectionRevoker
    {
        public void Disconnect(long userId, bool reconnect) => server.Disconnect(SessionCookieAuthenticator.ConnectionIdentifier(userId), reconnect);
    }
}
