using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers.Messages;

// reference/app/models/sound.rb and message_sound_presentation, against what the reference
// rendered for every sound (Vectors/generate.rb).
public sealed class SoundTests
{
    public static TheoryData<string> SoundNames() => [.. SeedMessages.GoldenList("sounds").Select(sound => sound.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(SoundNames))]
    public void Each_sound_presents_as_the_reference_presents_it(string name)
    {
        var view = new View { Assets = ReferenceAssets.Bundle, Origin = new UrlBase("http", SeedMessages.Host) };
        var golden = SeedMessages.GoldenList("sounds").Single(sound => sound.GetProperty("name").GetString() == name);

        Assert.Equal(golden.GetProperty("html").GetString(), view.MessageSoundPresentation(Sound.FindByName(name)!).ToString());
    }

    [Fact]
    public void Builtin_sounds_are_the_references_in_order() =>
        Assert.Equal(SoundNames().Select(row => (string)row.Data), Sound.Builtin.Select(sound => sound.Name));

    [Fact]
    public void Names_are_sorted_as_the_reference_sorts_them() =>
        Assert.Equal(SeedMessages.GoldenList("sound_names").Select(name => name.GetString()), Sound.Names);

    [Theory]
    [InlineData("/play bell", "bell")]
    [InlineData("/play 56k", "56k")]
    [InlineData("/play nope", null)]
    [InlineData("/play bell ", null)]
    [InlineData("/play bell\n", null)]
    [InlineData(" /play bell", null)]
    [InlineData("/playbell", null)]
    [InlineData("/play  bell", null)]
    public void A_message_plays_a_sound_only_for_exactly_play_and_a_known_name(string plainText, string? sound)
    {
        var message = SeedMessages.Output.Messages[0] with { PlainTextBody = plainText, Attachment = null };

        Assert.Equal(sound, message.Sound?.Name);
        Assert.Equal(sound is null ? "text" : "sound", message.ContentType);
    }
}
