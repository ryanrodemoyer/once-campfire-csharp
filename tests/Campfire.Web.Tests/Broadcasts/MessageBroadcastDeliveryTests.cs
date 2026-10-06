using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Cable.Tests.Server;
using Campfire.Cable.Turbo;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Signing;
using Campfire.Vectors;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Broadcasts;

/// <summary>
/// A message user A posts is broadcast on the room's message stream, and user B, subscribed to
/// that stream, receives the reference payload. <c>Turbo::StreamsChannel</c> will not make that
/// subscription (the leaked-name test covers that). B subscribes the way
/// <c>RoomMessagesChannel</c> will in RT03: the verified name, and only when B is a member.
/// </summary>
public sealed class MessageBroadcastDeliveryTests : IDisposable
{
    const long openRoom = 201306877;
    const long jason = 149087659;

    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/writes.json")))!;

    readonly MessagesApp messages = new(
        DateTimeOffset.Parse(Vectors["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        Vectors["fixtures"]!.AsArray().Select(fixture => fixture!.GetValue<string>()));

    public void Dispose() => messages.Dispose();

    [Fact]
    public async Task A_message_posted_by_user_A_reaches_user_B_with_the_reference_payload()
    {
        var gid = GlobalId.Create("Rooms::Open", openRoom).ToParam();
        var stream = $"{gid}:messages";
        var signed = TurboStreamName.SignedStreamName(messages.Keys, gid, "messages");
        var identifier = RailsJson.Generate(new JsonObject
        {
            ["channel"] = "MemberMessages",
            ["signed_stream_name"] = signed,
        });

        var jasonSession = await messages.Database.WriteAsync(
            transaction => Sessions.Start(transaction.Session, jason, "test", "127.0.0.1", messages.Now),
            TestContext.Current.CancellationToken);
        await using var cable = await CableTestApp.StartAsync(CableServer.Builder(
                new CableConfig { AssumeSsl = false },
                new SessionCookieAuthenticator(messages.Database, messages.Keys))
            .Channel(TurboStreams.ClassName, () => new TurboStreamsChannel<User>(messages.Keys))
            .Channel("MemberMessages", () => new MemberMessagesChannel(messages.Database, messages.Keys))
            .Build());

        using var client = await cable.ConnectAsync(messages.SignedIn(jasonSession.Token)["Cookie"]);
        Assert.Equal("""{"type":"welcome"}""", await client.NextAsync());
        await client.SubscribeAsync(identifier);
        Assert.Equal(Confirm(identifier), await client.NextAsync());

        var sample = Vectors["cases"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "member creates in an open room")!;
        var request = sample["request"]!;
        var response = await messages.SendAsync(
            request["method"]!.GetValue<string>(),
            request["path"]!.GetValue<string>(),
            request["headers"]!.AsObject().ToDictionary(header => header.Key, header => header.Value!.GetValue<string>()),
            request["body"]?.GetValue<string>(),
            request["content_type"]?.GetValue<string>());
        Assert.Equal(200, response.Status);

        var expected = sample["events"]!.AsArray()
            .Single(item => item!["broadcast"]?.GetValue<string>() == stream)!["payload"]!.GetValue<string>();
        var broadcast = Assert.Single(messages.Seams.Broadcasts, item => item.Stream == stream);
        Assert.Equal(expected, broadcast.Payload);

        cable.Server.Broadcast(broadcast.Stream, broadcast.Payload);
        Assert.Equal(Message(identifier, expected), await client.NextAsync());
    }

    static string Confirm(string identifier) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"type":"confirm_subscription"}""";

    static string Message(string identifier, string message) => $$"""{"identifier":{{RailsJson.Generate(JsonValue.Create(identifier))}},"message":{{message}}}""";

    /// <summary>
    /// The membership half of <c>RoomMessagesChannel#subscribed</c>, enough for B to be on the
    /// stream. The production channel is RT03's, under <c>src/Campfire.Cable/Channels/</c>.
    /// </summary>
    sealed class MemberMessagesChannel(SqliteDatabase database, KeyGenerator keys) : Channel<User>
    {
        public override async ValueTask SubscribedAsync()
        {
            var streamName = TurboStreams.VerifiedStreamName(keys, Params);
            var roomId = streamName is null ? null : RoomId(streamName);
            var member = roomId is { } id && await database.ReadAsync(
                session => Memberships.FindFor(session, CurrentUser.Id, id) is not null,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            if (streamName is not null && RoomStreams.IsGuarded(streamName) && member)
            {
                StreamFrom(streamName);
            }
            else
            {
                Reject();
            }
        }

        static long? RoomId(string streamName)
        {
            var gid = GlobalId.FromParam(streamName.Split(':', 2)[0]);
            if (gid is null || !long.TryParse(gid.Id, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                return null;
            }

            return gid.ModelName is "Room" or "Rooms::Open" or "Rooms::Closed" or "Rooms::Direct" ? id : null;
        }
    }
}
