using System.Text.Json.Nodes;
using Campfire.Vectors;

namespace Campfire.RichText.Fuzz;

/// <summary>
/// The rich text gate: every case in <c>vectors/richtext/expected.json</c>, on all six outputs
/// <c>reference-tools/richtext/generate.rb</c> recorded from the reference, the editor value at this
/// repository's pin (<see cref="RichTextVectors"/>).
/// </summary>
public class ReferenceCaseGateTests
{
    static readonly Lazy<Dictionary<string, JsonNode>> ReferenceCases = new(() =>
    {
        var cases = JsonNode.Parse(VectorFiles.ReadBytes("richtext/expected.json"))!["cases"]!.AsArray()
            .ToDictionary(c => c!["name"]!.GetValue<string>(), c => c!);
        foreach (var edited in JsonNode.Parse(File.ReadAllBytes(Path.Combine(VectorFiles.Root, RichTextVectors.EditablePath)))!["cases"]!.AsArray())
        {
            cases[edited!["name"]!.GetValue<string>()]["editable"] = edited["editable"]!.DeepClone();
        }
        return cases;
    });

    public static TheoryData<string> Cases() => [.. RichTextVectors.File.Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_output_equals_the_references(string name)
    {
        var vector = RichTextVectors.File.Cases.Single(c => c.Name == name);
        var expected = PipelineOutputs.FromReference(ReferenceCases.Value[name]);
        var actual = PipelineOutputs.FromPort(vector.Body, VectorRecords.Context(vector.Host));

        var disagreements = actual.Disagreements(expected);
        if (RichTextVectors.KnownDifference(vector.Body) is not null)
        {
            // EditableVectorTests skips this case's editor value, citing the task that fixes it
            disagreements.Remove("editable");
        }
        Assert.Equal([], Describe(disagreements, expected, actual));
    }

    [Fact]
    public void Covers_all_658_cases_with_unique_names()
    {
        Assert.Equal(658, RichTextVectors.File.Cases.Count);
        Assert.Equal(658, ReferenceCases.Value.Count);
    }

    static List<string> Describe(List<string> names, PipelineOutputs expected, PipelineOutputs actual)
    {
        var lookup = expected.Each().Zip(actual.Each()).ToDictionary(p => p.First.Name);
        return [.. names.Select(n => $"{n}: reference {lookup[n].First.Output}, port {lookup[n].Second.Output}")];
    }
}
