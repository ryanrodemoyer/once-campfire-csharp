using System.Text;
using System.Text.Json;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.GlobalIdUnitTests;

/// <summary>
/// <c>URI::GID.parse</c> as the reference runs it (globalid 1.4.0, Ruby 4.0's RFC 3986 parser), from
/// <c>Vectors/parse.json</c>, which <c>Vectors/generate.rb</c> writes in the reference image.
/// </summary>
public class GlobalIdParseVectorTests
{
    public sealed record ParseCase(string Gid, string? App = null, string? ModelName = null, JsonElement? ModelId = null, string? Error = null, string? Raises = null);

    sealed record ParseFile(JsonElement Versions, IReadOnlyList<ParseCase> Cases);

    static readonly Lazy<ParseFile> File = new(() => VectorFiles.Parse<ParseFile>(System.IO.File.ReadAllBytes(
        Path.Combine(VectorFiles.Root, "tests/Campfire.RailsCompat.Tests/GlobalId/Vectors/parse.json"))));

    public static TheoryData<ParseCase> Cases() => VectorFiles.Rows(File.Value.Cases, c => JsonSerializer.Serialize(c.Gid));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parses_as_the_reference_does(ParseCase c)
    {
        Assert.Null(c.Raises);
        var gid = Campfire.RailsCompat.GlobalId.GlobalId.Parse(c.Gid);

        if (c.Error is not null)
        {
            Assert.Null(gid);
            return;
        }

        Assert.NotNull(gid);
        Assert.Equal(c.App, gid.App);
        Assert.Equal(c.ModelName, gid.ModelName);
        var ids = c.ModelId!.Value.ValueKind == JsonValueKind.Array ? c.ModelId.Value.EnumerateArray().Select(Text).ToList() : [Text(c.ModelId.Value)];
        Assert.Equal(c.ModelId.Value.ValueKind == JsonValueKind.Array, gid.Composite);
        Assert.Equal(string.Join('/', ids), gid.Id);
    }

    // A model id that isn't valid UTF-8 comes as its bytes; the port holds it decoded lossily
    static string Text(JsonElement id) => id.ValueKind == JsonValueKind.String
        ? id.GetString()!
        : Encoding.UTF8.GetString([.. id.GetProperty("bytes").EnumerateArray().Select(b => b.GetByte())]);
}
