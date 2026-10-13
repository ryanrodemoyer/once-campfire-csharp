using System.Text.Json;
using Campfire.RichText.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Attachments;

/// <summary>
/// <see cref="RubyJsonParser"/> against json 2.21.2 in the reference image (<c>Vectors/json_parse.json</c>,
/// written by <c>Vectors/generate_json.rb</c>): what it accepts, and for a failure whether the
/// message is valid UTF-8, which decides whether <c>message_presentation</c> can log it.
/// </summary>
public class RubyJsonParserTests
{
    public sealed record JsonCase(string Input, string? Ok = null, string? Error = null, bool? Loggable = null, string? Message = null);

    sealed record JsonFile(string Json, IReadOnlyList<JsonCase> Cases);

    static readonly Lazy<JsonFile> File = new(() => VectorFiles.Parse<JsonFile>(System.IO.File.ReadAllBytes(
        Path.Combine(VectorFiles.Root, "tests/Campfire.RichText.Tests/Attachments/Vectors/json_parse.json"))));

    public static TheoryData<JsonCase> Cases() => VectorFiles.Rows(File.Value.Cases, c => c.Input);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parses_as_the_reference_does(JsonCase c)
    {
        var input = Convert.FromBase64String(c.Input);

        if (c.Error is not null)
        {
            var error = Assert.Throws<RichTextRaisedException>(() => RubyJsonParser.Parse(input));
            Assert.StartsWith(c.Error, error.Message, StringComparison.Ordinal);
            Assert.Equal(!c.Loggable, error.Unloggable);
            return;
        }

        var value = RubyJsonParser.Parse(input);
        // A value holding a string that isn't valid UTF-8 can't be written back by Ruby
        if (c.Ok is not null)
        {
            using var expected = JsonDocument.Parse(c.Ok, new JsonDocumentOptions { MaxDepth = 128 });
            Assert.True(SameValue(expected.RootElement, value), $"{c.Ok} != {value.GetRawText()}");
        }
    }

    // Ruby writes floats its own way (1E+5 is 100000.0); objects compare by their last value for a key
    static bool SameValue(JsonElement expected, JsonElement actual) => (expected.ValueKind, actual.ValueKind) switch
    {
        (JsonValueKind.Object, JsonValueKind.Object) =>
            expected.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(actual.EnumerateObject().Select(p => p.Name).Distinct().Order())
            && expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var v) && SameValue(p.Value, v)),
        (JsonValueKind.Array, JsonValueKind.Array) =>
            expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(p => SameValue(p.First, p.Second)),
        (JsonValueKind.Number, JsonValueKind.Number) => expected.GetDouble() == actual.GetDouble(),
        (JsonValueKind.String, JsonValueKind.String) => expected.GetString() == actual.GetString(),
        var (e, a) => e == a,
    };
}
