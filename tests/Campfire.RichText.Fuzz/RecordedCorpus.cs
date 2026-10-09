using System.IO.Compression;
using System.Text.Json.Nodes;
using Campfire.Vectors;

namespace Campfire.RichText.Fuzz;

/// <summary>A fuzz case recorded with the reference's outputs.</summary>
public sealed record RecordedCase(FuzzCase Case, PipelineOutputs Reference);

/// <summary>
/// <c>Corpus/recorded.jsonl.gz</c>: fuzz cases recorded from a live run with the reference's
/// outputs (<see cref="Recorder"/>), so every <c>bin/check</c> replays them against the port
/// without Docker. The first line is the oracle's records and when they were recorded, which is
/// the time SGID expiry is checked against.
/// </summary>
public static class RecordedCorpus
{
    public static string PathOf(string name) => Path.Combine(VectorFiles.Root, "tests", "Campfire.RichText.Fuzz", "Corpus", name);

    public static (OracleRecords Records, DateTimeOffset RecordedAt, List<RecordedCase> Cases) Load(string path)
    {
        using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        var header = reader.ReadLine()!;
        var recordedAt = DateTimeOffset.Parse(JsonNode.Parse(header)!["recorded_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var cases = new List<RecordedCase>();
        while (reader.ReadLine() is { } line)
        {
            var node = JsonNode.Parse(line)!;
            cases.Add(new RecordedCase(
                new FuzzCase(node["index"]!.GetValue<long>(), node["family"]!.GetValue<string>(), node["host"]!.GetValue<string>(), node["body"]!.GetValue<string>()),
                PipelineOutputs.FromReference(node["reference"]!)));
        }
        return (OracleRecords.Parse(header), recordedAt, cases);
    }

    /// <summary>The outputs in the oracle's own shape, so they read back with <see cref="PipelineOutputs.FromReference"/>.</summary>
    public static JsonObject ToJson(PipelineOutputs outputs)
    {
        var json = new JsonObject();
        foreach (var (name, output) in outputs.Each())
        {
            if (name == "presentation_raised")
            {
                json[name] = output.Raised ? output.Error : null;
            }
            else
            {
                json[name] = output.Raised ? new JsonObject { ["error"] = output.Error } : new JsonObject { ["ok"] = Value(name, output.Value) };
            }
        }
        return json;
    }

    // Mentioned ids are a JSON array; everything else a string or nil
    static JsonNode? Value(string name, string? value) =>
        name == "mentioned" && value is not null ? JsonNode.Parse(value) : value is null ? null : JsonValue.Create(value);
}
