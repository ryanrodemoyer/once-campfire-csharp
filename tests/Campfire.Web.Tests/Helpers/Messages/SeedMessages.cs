using System.Text.Json;
using Campfire.Data.Queries;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Attachments;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers.Messages;

/// <summary>
/// The default parity seed's messages, loaded through <see cref="MessageViews"/> and rendered by
/// the partials, next to what the reference rendered for the same seed (<c>Vectors/messages.json</c>,
/// written by <c>Vectors/generate.rb</c>). Everything renders in one read of a copy of the seed
/// (<c>tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3</c>).
/// </summary>
static class SeedMessages
{
    // parity/.env.reference
    const string secretKeyBase = "5335c3b1ad35b4ad170c3413bd651ef3b6ed64e257261871a6de3f978cf3868ee417a927040935fb30b0f7debdedb34a2a403e9f34b16cf594c917c2ecd4a995";

    public const string Host = "campfire.test";

    static readonly KeyGenerator Keys = new(secretKeyBase);

    public static readonly JsonElement Golden = LoadGolden();

    static readonly Lazy<Rendered> LazyRendered = new(Render);

    public static Rendered Output => LazyRendered.Value;

    public static IEnumerable<JsonElement> GoldenList(string name) => Golden.GetProperty(name).EnumerateArray();

    public static JsonElement GoldenFor(string name, string key, long id) =>
        GoldenList(name).Single(item => item.GetProperty(key).GetInt64() == id);

    /// <summary>What the partials rendered for the seed.</summary>
    public sealed record Rendered(
        IReadOnlyList<MessageView> Messages,
        IReadOnlyDictionary<long, string> MessageHtml,
        IReadOnlyDictionary<long, string> MessageJson,
        IReadOnlyDictionary<long, string> BoostHtml,
        IReadOnlyDictionary<long, string> BoostJson,
        IReadOnlyDictionary<long, string> TemplateHtml,
        string Unrenderable,
        IReadOnlyDictionary<string, (string Html, string Json)> Scenarios);

    /// <summary>A view for a request to http://campfire.test, as the generator's renderer made.</summary>
    static View NewView(SqliteSession session) => new()
    {
        Assets = ReferenceAssets.Bundle,
        Origin = new UrlBase("http", Host),
        RichTextContext = new RenderContext(new DatabaseAttachables(session, Keys, DateTimeOffset.UtcNow), Host),
        Storage = BlobStorage.Local(Path.GetTempPath(), Keys),
    };

    static Rendered Render()
    {
        // A copy, since opening a database prepares it.
        var path = Path.Combine(Path.GetTempPath(), $"campfire-web-tests-{Guid.NewGuid():N}.sqlite3");
        File.Copy(Path.Combine(ReferenceAssets.RepositoryRoot, "tests", "Campfire.Data.Tests", "Oracle", "parity-seed.sqlite3"), path);
        try
        {
            using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(path) { Readers = 1 });
            var rendered = database.ReadAsync(Render).GetAwaiter().GetResult();
            database.WriteAsync(MakeScenarios).GetAwaiter().GetResult();
            return rendered with { Scenarios = database.ReadAsync(RenderScenarios).GetAwaiter().GetResult() };
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    static Rendered Render(SqliteSession session)
    {
        var views = new MessageViews(Keys, PlainTextFromGolden(session));
        var ids = GoldenList("messages").Select(message => message.GetProperty("id").GetInt64()).ToList();
        var messages = views.Load(session, [.. Campfire.Data.Queries.Messages.WhereIds(session, ids).OrderBy(message => message.Id)]);
        var boosts = messages.SelectMany(message => message.Boosts).ToList();
        var users = Users.WhereIds(session, [.. GoldenList("templates").Select(template => template.GetProperty("user_id").GetInt64())]);

        var view = NewView(session);
        return new Rendered(
            messages,
            messages.ToDictionary(message => message.Id, message => HelperGoldenTests.Render(w => view.MessagesMessage(w, message))),
            messages.ToDictionary(message => message.Id, view.MessageJson),
            boosts.ToDictionary(boost => boost.Id, boost => HelperGoldenTests.Render(w => view.MessagesBoostsBoost(w, boost))),
            boosts.ToDictionary(boost => boost.Id, view.BoostJson),
            users.ToDictionary(user => user.Id, user => HelperGoldenTests.Render(w => view.MessagesTemplate(w, views.User(user)))),
            HelperGoldenTests.Render(view.MessagesUnrenderable),
            new Dictionary<string, (string, string)>());
    }

    // The states generate.rb makes after its renders: a booster whose user is gone, a direct room
    // with no members left, and a video without dimensions.
    static void MakeScenarios(WriteTransaction transaction)
    {
        var session = transaction.Session;
        session.Execute("UPDATE boosts SET booster_id = 999998 WHERE id = @id", ("@id", Scenario("gone_booster").GetProperty("boost_id").GetInt64()));
        session.Execute("DELETE FROM memberships WHERE room_id = @id", ("@id", Scenario("memberless_direct_room").GetProperty("room_id").GetInt64()));
        var blob = BlobRecords.FindBlob(session, Scenario("video_without_dimensions").GetProperty("blob_id").GetInt64())!;
        var metadata = (System.Text.Json.Nodes.JsonObject)blob.Metadata.DeepClone();
        metadata.Remove("width");
        metadata.Remove("height");
        BlobRecords.UpdateMetadata(session, blob.Id, metadata);
    }

    static Dictionary<string, (string Html, string Json)> RenderScenarios(SqliteSession session)
    {
        var views = new MessageViews(Keys, PlainTextFromGolden(session));
        var view = NewView(session);
        return GoldenList("scenarios").ToDictionary(scenario => scenario.GetProperty("name").GetString()!, scenario =>
        {
            var message = views.Load(session, [Campfire.Data.Queries.Messages.Find(session, scenario.GetProperty("message_id").GetInt64())!])[0];
            return (HelperGoldenTests.Render(w => view.MessagesMessage(w, message)), view.MessageJson(message));
        });
    }

    static JsonElement Scenario(string name) => GoldenList("scenarios").Single(scenario => scenario.GetProperty("name").GetString() == name);

    // `to_plain_text` is R05's: the reference's plain_text_body of the message with each body.
    static Func<string, string> PlainTextFromGolden(SqliteSession session)
    {
        var plainTexts = GoldenList("messages").ToDictionary(
            message => message.GetProperty("id").GetInt64(), message => message.GetProperty("plain_text_body").GetString()!);
        var byBody = new Dictionary<string, string>();
        foreach (var richText in RichTexts.ForRecords(session, "Message", "body", [.. plainTexts.Keys]))
        {
            byBody[richText.Body ?? ""] = plainTexts[richText.RecordId];
        }
        return body => byBody[body];
    }

    static JsonElement LoadGolden()
    {
        var path = Path.Combine(ReferenceAssets.RepositoryRoot, "tests", "Campfire.Web.Tests", "Helpers", "Messages", "Vectors", "messages.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
