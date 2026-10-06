namespace Campfire.Web.Tests.Helpers.Messages;

// Every message, boost and composer template in the default parity seed renders the bytes the
// reference's partials rendered for it (SeedMessages, Vectors/generate.rb).
public sealed class MessageRenderingTests
{
    public static TheoryData<long> MessageIds() => [.. SeedMessages.GoldenList("messages").Select(message => message.GetProperty("id").GetInt64())];

    public static TheoryData<long> BoostIds() => [.. SeedMessages.GoldenList("boosts").Select(boost => boost.GetProperty("id").GetInt64())];

    public static TheoryData<long> UserIds() => [.. SeedMessages.GoldenList("templates").Select(template => template.GetProperty("user_id").GetInt64())];

    [Theory]
    [MemberData(nameof(MessageIds))]
    public void Each_seed_message_renders_as_the_reference_renders_it(long id) =>
        Assert.Equal(Golden("messages", "id", id, "html"), SeedMessages.Output.MessageHtml[id]);

    [Theory]
    [MemberData(nameof(MessageIds))]
    public void Each_seed_message_json_is_the_references(long id) =>
        Assert.Equal(Golden("messages", "id", id, "json"), SeedMessages.Output.MessageJson[id]);

    [Theory]
    [MemberData(nameof(BoostIds))]
    public void Each_seed_boost_renders_as_the_reference_renders_it(long id) =>
        Assert.Equal(Golden("boosts", "id", id, "html"), SeedMessages.Output.BoostHtml[id]);

    [Theory]
    [MemberData(nameof(BoostIds))]
    public void Each_seed_boost_json_is_the_references(long id) =>
        Assert.Equal(Golden("boosts", "id", id, "json"), SeedMessages.Output.BoostJson[id]);

    [Theory]
    [MemberData(nameof(UserIds))]
    public void The_message_template_renders_for_each_user_as_the_reference_renders_it(long userId) =>
        Assert.Equal(Golden("templates", "user_id", userId, "html"), SeedMessages.Output.TemplateHtml[userId]);

    public static TheoryData<string> ScenarioNames() => [.. SeedMessages.GoldenList("scenarios").Select(scenario => scenario.GetProperty("name").GetString()!)];

    // States made from the seed on both sides: a booster whose user is gone (unrenderable), a
    // direct room with no members (the link shows its URL), a video without dimensions.
    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Each_scenario_renders_as_the_reference_renders_it(string name)
    {
        var golden = SeedMessages.GoldenList("scenarios").Single(scenario => scenario.GetProperty("name").GetString() == name);
        var (html, json) = SeedMessages.Output.Scenarios[name];
        Assert.Equal(golden.GetProperty("html").GetString(), html);
        Assert.Equal(golden.GetProperty("json").GetString(), json);
    }

    [Fact]
    public void Unrenderable_is_the_references() =>
        Assert.Equal(SeedMessages.Golden.GetProperty("unrenderable").GetString(), SeedMessages.Output.Unrenderable);

    [Fact]
    public void The_seed_covers_every_kind_of_message()
    {
        var messages = SeedMessages.Output.Messages;
        Assert.Equal(SeedMessages.GoldenList("messages").Count(), messages.Count);
        Assert.Contains(messages, message => message.ContentType == "sound" && message.Sound!.Image is not null);
        Assert.Contains(messages, message => message.ContentType == "sound" && message.Sound!.Text is not null);
        Assert.Contains(messages, message => message.Attachment is { IsVideo: true });
        Assert.Contains(messages, message => message.Attachment is { IsVariable: true });
        Assert.Contains(messages, message => message.Attachment is { IsVariable: false, IsVideo: false });
        Assert.Contains(messages, message => message.IsAllEmoji);
        Assert.Contains(messages, message => message.Creator is null);
        Assert.Contains(messages, message => message.Creator?.Role == Campfire.Data.Records.UserRole.Bot);
        Assert.Contains(messages, message => message.Boosts.Count > 1);
        Assert.Contains(messages, message => message.Body?.Contains("action-text-attachment", StringComparison.Ordinal) == true);
    }

    static string Golden(string list, string key, long id, string property) =>
        SeedMessages.GoldenFor(list, key, id).GetProperty(property).GetString()!;
}
