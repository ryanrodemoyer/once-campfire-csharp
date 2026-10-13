using System.Text.Json;
using Campfire.RichText.Sanitize;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Sanitize;

/// <summary>
/// <see cref="CssScrubber"/> against <c>Loofah::HTML5::Scrub.scrub_css</c> in the reference image
/// (<c>Vectors/css_scrub.json</c>, written by <c>Vectors/generate_css.rb</c>).
/// </summary>
public class CssScrubberTests
{
    public sealed record CssCase(string Style, string Scrubbed);

    sealed record CssFile(string Loofah, IReadOnlyList<CssCase> Cases);

    static readonly Lazy<CssFile> File = new(() => VectorFiles.Parse<CssFile>(System.IO.File.ReadAllBytes(
        Path.Combine(VectorFiles.Root, "tests/Campfire.RichText.Tests/Sanitize/Vectors/css_scrub.json"))));

    public static TheoryData<CssCase> Cases() => VectorFiles.Rows(File.Value.Cases, c => JsonSerializer.Serialize(c.Style));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Scrubs_as_loofah_does(CssCase c)
    {
        Assert.Equal(c.Scrubbed, CssScrubber.Scrub(c.Style));
    }
}
