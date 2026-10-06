using System.Text.Json.Nodes;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests.Queries;

// Every case in Oracle/queries.json: what the reference app's scope or query returned on the
// parity seed, against what the C# query returns on the same database.
public class QueryOracleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record OracleCase(string Query, JsonArray Args, JsonNode? Result);

    static readonly Lazy<List<OracleCase>> Cases = new(() =>
        JsonNode.Parse(File.ReadAllText(TestDatabase.Oracle("queries.json")))!.AsArray()
            .Select(node => new OracleCase(node!["query"]!.GetValue<string>(), node["args"]!.AsArray(), node["result"]))
            .ToList());

    public static TheoryData<string> QueryNames() => [.. Cases.Value.Select(c => c.Query).Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_oracle_query_has_a_csharp_counterpart()
    {
        var missing = QueryNames().Select(row => row.Data).Except(Queries.Keys).ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(QueryNames))]
    public async Task Query_returns_what_rails_returns(string query)
    {
        using var file = TestDatabase.FromParitySeed();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        var now = DateTimeOffset.UtcNow;

        var mismatches = await database.ReadAsync(session =>
            Cases.Value.Where(c => c.Query == query)
                .Select(c => (c.Args, Expected: c.Result?.ToJsonString(), Actual: Queries[query](session, c.Args, now)?.ToJsonString()))
                .Where(c => c.Expected != c.Actual)
                .Select(c => $"{query}({c.Args.ToJsonString()}): rails {c.Expected}, c# {c.Actual}")
                .ToList(), Ct);

        Assert.Empty(mismatches);
    }

    delegate JsonNode? Run(SqliteSession session, JsonArray args, DateTimeOffset now);

    static readonly Dictionary<string, Run> Queries = new()
    {
        ["Users.ActiveOrdered"] = (s, a, _) => Ids(Users.ActiveOrdered(s)),
        ["Users.ActiveIds"] = (s, a, _) => Ids(Users.ActiveIds(s)),
        ["Users.ActiveOrderedWithoutBots"] = (s, a, _) => Ids(Users.ActiveOrderedWithoutBots(s)),
        ["Users.WithStatusesOrderedWithoutBots"] = (s, a, _) => Ids(Users.WithStatusesOrderedWithoutBots(s, [.. LongList(a[0]).Select(status => (UserStatus)status)])),
        ["Users.ActiveBotsOrdered"] = (s, a, _) => Ids(Users.ActiveBotsOrdered(s)),
        ["Users.Find"] = (s, a, _) => Id(Users.Find(s, Long(a[0]))),
        ["Users.FindActive"] = (s, a, _) => Id(Users.FindActive(s, Long(a[0]))),
        ["Users.FindActiveBot"] = (s, a, _) => Id(Users.FindActiveBot(s, Long(a[0]))),
        ["Users.FindActiveByEmailAddress"] = (s, a, _) => Id(Users.FindActiveByEmailAddress(s, Text(a[0])!)),
        ["Users.ActiveFilteredByOrdered"] = (s, a, _) => Ids(Users.ActiveFilteredByOrdered(s, Text(a[0])!)),
        ["Users.AuthenticateBot"] = (s, a, _) => Id(Users.AuthenticateBot(s, Text(a[0])!)),
        ["Users.ActiveExcludingByCreation"] = (s, a, _) => Ids(Users.ActiveExcludingByCreation(s, LongList(a[0]), (int)Long(a[1]))),
        ["Users.InRoom"] = (s, a, _) => Ids(Users.InRoom(s, Long(a[0]))),
        ["Users.IdsInRoom"] = (s, a, _) => Ids(Users.IdsInRoom(s, Long(a[0]))),
        ["Users.ActiveBotsInRoom"] = (s, a, _) => Ids(Users.ActiveBotsInRoom(s, Long(a[0]))),
        ["Users.InRoomWhereIds"] = (s, a, _) => Ids(Users.InRoomWhereIds(s, Long(a[0]), LongList(a[1]))),

        ["Rooms.Count"] = (s, a, _) => Rooms.Count(s),
        ["Rooms.Original"] = (s, a, _) => Id(Rooms.Original(s)),
        ["Rooms.OfType"] = (s, a, _) => Ids(Rooms.OfType(s, Type(a[0]))),
        ["Rooms.IdsOfType"] = (s, a, _) => Ids(Rooms.IdsOfType(s, Type(a[0]))),
        ["Rooms.CountOfType"] = (s, a, _) => Rooms.CountOfType(s, Type(a[0])),
        ["Rooms.ForUser"] = (s, a, _) => Ids(Rooms.ForUser(s, Long(a[0]))),
        ["Rooms.OriginalForUser"] = (s, a, _) => Id(Rooms.OriginalForUser(s, Long(a[0]))),
        ["Rooms.ForUserWithoutDirects"] = (s, a, _) => Ids(Rooms.ForUserWithoutDirects(s, Long(a[0]))),
        ["Rooms.ForUserWithoutDirectsOrdered"] = (s, a, _) => Ids(Rooms.ForUserWithoutDirectsOrdered(s, Long(a[0]))),
        ["Rooms.ForUserOfType"] = (s, a, _) => Ids(Rooms.ForUserOfType(s, Long(a[0]), Type(a[1]))),
        ["Rooms.IdsForUserOfType"] = (s, a, _) => Ids(Rooms.IdsForUserOfType(s, Long(a[0]), Type(a[1]))),
        ["Rooms.FindForUser"] = (s, a, _) => Id(Rooms.FindForUser(s, Long(a[0]), Long(a[1]))),
        ["Rooms.FindForUserWithoutDirects"] = (s, a, _) => Id(Rooms.FindForUserWithoutDirects(s, Long(a[0]), Long(a[1]))),
        ["Rooms.FindDirectFor"] = (s, a, _) => Id(Rooms.FindDirectFor(s, LongList(a[0]))),

        ["Memberships.Count"] = (s, a, _) => Memberships.Count(s),
        ["Memberships.Connected"] = (s, a, now) => Ids(Memberships.Connected(s, now)),
        ["Memberships.Disconnected"] = (s, a, now) => Ids(Memberships.Disconnected(s, now)),
        ["Memberships.ForUser"] = (s, a, _) => Ids(Memberships.ForUser(s, Long(a[0]))),
        ["Memberships.ForRoom"] = (s, a, _) => Ids(Memberships.ForRoom(s, Long(a[0]))),
        ["Memberships.ForUserWithOrderedRoom"] = (s, a, _) =>
            Ids(Memberships.ForUserWithOrderedRoom(s, Long(a[0]), a[1]!.GetValue<bool>()).Select(pair => pair.Membership.Id)),
        ["Memberships.CountUnreadForUser"] = (s, a, _) => Memberships.CountUnreadForUser(s, Long(a[0])),
        ["Memberships.CountForUserWithoutDirectRooms"] = (s, a, _) => Memberships.CountForUserWithoutDirectRooms(s, Long(a[0])),
        ["Memberships.UserIdsInRooms"] = (s, a, _) => Ids(Memberships.UserIdsInRooms(s, LongList(a[0]))),
        ["Memberships.FindFor"] = (s, a, _) => Id(Memberships.FindFor(s, Long(a[0]), Long(a[1]))),

        ["Messages.InRoomOrdered"] = (s, a, _) => Ids(Messages.InRoomOrdered(s, Long(a[0]))),
        ["Messages.CountInRoom"] = (s, a, _) => Messages.CountInRoom(s, Long(a[0])),
        ["Messages.ByCreator"] = (s, a, _) => Ids(Messages.ByCreator(s, Long(a[0]))),
        ["Messages.WhereIds"] = (s, a, _) => Ids(Messages.WhereIds(s, LongList(a[0]))),
        ["Boosts.ForMessageOrdered"] = (s, a, _) => Ids(Boosts.ForMessageOrdered(s, Long(a[0]))),
        ["Boosts.ByBooster"] = (s, a, _) => Ids(Boosts.ByBooster(s, Long(a[0]))),
        ["RichTexts.For"] = (s, a, _) => Id(RichTexts.For(s, Text(a[0])!, Long(a[1]), Text(a[2])!)),
        ["Attachments.For"] = (s, a, _) => Id(Attachments.For(s, Text(a[0])!, Long(a[1]), Text(a[2])!)),
        ["Attachments.ForBlob"] = (s, a, _) => Ids(Attachments.ForBlob(s, Long(a[0]))),
        ["VariantRecords.ForBlobs"] = (s, a, _) => Ids(VariantRecords.ForBlobs(s, LongList(a[0]))),

        ["PushSubscriptions.ForRoomPush"] = (s, a, now) => Ids(PushSubscriptions.ForRoomPush(
            s, Long(a[0]), Long(a[1]), Involvements.FromName(Text(a[2]))!.Value, now, a[3] is null ? null : LongList(a[3]))),
        ["PushSubscriptions.ForUser"] = (s, a, _) => Ids(PushSubscriptions.ForUser(s, Long(a[0]))),

        ["Sessions.FindByToken"] = (s, a, _) => Id(Sessions.FindByToken(s, Text(a[0])!)),
        ["Sessions.ForUser"] = (s, a, _) => Ids(Sessions.ForUser(s, Long(a[0]))),
        ["Sessions.IpAddressesForUser"] = (s, a, _) => new JsonArray([.. Sessions.IpAddressesForUser(s, Long(a[0])).Select(ip => (JsonNode?)ip)]),
        ["Bans.ForUser"] = (s, a, _) => Ids(Bans.ForUser(s, Long(a[0]))),
        ["Bans.IsBanned"] = (s, a, _) => Bans.IsBanned(s, Text(a[0])),
        ["Searches.ForUserOrdered"] = (s, a, _) => Ids(Searches.ForUserOrdered(s, Long(a[0]))),
        ["Webhooks.ForUser"] = (s, a, _) => Id(Webhooks.ForUser(s, Long(a[0]))),
        ["Accounts.First"] = (s, a, _) => Id(Accounts.First(s)),
        ["Account.Settings"] = (s, a, _) =>
        {
            var settings = Accounts.Find(s, Long(a[0]))!.SettingsData;
            return new JsonObject { ["json"] = settings.ToJson(), ["restrict"] = settings.RestrictRoomCreationToAdministrators };
        },
    };

    static long Long(JsonNode? node) => node!.GetValue<long>();

    static string? Text(JsonNode? node) => node?.GetValue<string>();

    static List<long> LongList(JsonNode? node) => [.. node!.AsArray().Select(Long)];

    static RoomType Type(JsonNode? node) => Enum.Parse<RoomType>(Text(node)!);

    static JsonNode? Id(object? record) => record switch
    {
        null => null,
        User user => user.Id,
        Room room => room.Id,
        Membership membership => membership.Id,
        Session session => session.Id,
        Webhook webhook => webhook.Id,
        Account account => account.Id,
        ActionTextRichText richText => richText.Id,
        ActiveStorageAttachment attachment => attachment.Id,
        _ => throw new ArgumentException($"no id for {record.GetType()}", nameof(record)),
    };

    static JsonArray Ids<T>(IEnumerable<T> records) =>
        new([.. records.Select(record => record is long id ? JsonValue.Create(id) : record switch
        {
            Message message => JsonValue.Create(message.Id),
            Boost boost => JsonValue.Create(boost.Id),
            Ban ban => JsonValue.Create(ban.Id),
            Search search => JsonValue.Create(search.Id),
            PushSubscription subscription => JsonValue.Create(subscription.Id),
            ActiveStorageVariantRecord variant => JsonValue.Create(variant.Id),
            ActiveStorageAttachment attachment => JsonValue.Create(attachment.Id),
            Session session => JsonValue.Create(session.Id),
            _ => (JsonNode?)Id(record),
        })]);
}
