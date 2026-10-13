using System.Text.Json;
using Campfire.RichText.Sanitize;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Sanitize;

/// <summary>
/// <see cref="SafeListSanitizer.AllowedUri"/> against <c>Loofah::HTML5::Scrub.allowed_uri?</c> in the
/// reference image (<c>Vectors/allowed_uri.json</c>, written by <c>Vectors/generate_allowed_uri.rb</c>).
/// </summary>
public class AllowedUriTests
{
    public sealed record UriCase(string Uri, bool Allowed);

    sealed record UriFile(string Loofah, IReadOnlyList<UriCase> Cases);

    static readonly Lazy<UriFile> File = new(() => VectorFiles.Parse<UriFile>(System.IO.File.ReadAllBytes(
        Path.Combine(VectorFiles.Root, "tests/Campfire.RichText.Tests/Sanitize/Vectors/allowed_uri.json"))));

    public static TheoryData<UriCase> Cases() => VectorFiles.Rows(File.Value.Cases, c => JsonSerializer.Serialize(c.Uri));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Allows_what_loofah_allows(UriCase c)
    {
        Assert.Equal(c.Allowed, SafeListSanitizer.AllowedUri(c.Uri));
    }
}
