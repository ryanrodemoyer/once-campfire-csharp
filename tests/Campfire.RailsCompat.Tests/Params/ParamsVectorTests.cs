using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Params;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Params;

/// <summary>
/// Differential check against <c>ActionDispatch::ParamBuilder.from_query_string</c>, using the
/// vectors the Rust port generated in the reference container
/// (<c>reference-rust/crates/kit/tests/params_vectors.rb</c>): 58 hand-written query strings and
/// ~2,700 random ones. A null output means Rails raised (a 400).
/// </summary>
public class ParamsVectorTests
{
    static readonly string VectorsPath = Path.Combine(VectorFiles.Root, "reference-rust", "crates", "kit", "tests", "params_vectors.json");

    [Fact]
    public void QueryStringsParseLikeRails()
    {
        var vectors = JsonNode.Parse(File.ReadAllBytes(VectorsPath), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsArray();
        Assert.True(vectors.Count > 2700, $"only {vectors.Count} vectors");

        var failures = new List<string>();
        foreach (var vector in vectors)
        {
            var input = vector!["input"]!.GetValue<string>();
            var expected = vector["output"]?.ToJsonString(Canonical) ?? "null";
            var actual = Parse(input);
            if (actual != expected)
            {
                failures.Add($"{JsonSerializer.Serialize(input)}\n  rails: {expected}\n  ours:  {actual}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {vectors.Count} differ:\n{string.Join('\n', failures.Take(50))}");
    }

    // The tree as order-preserving JSON, or "null" when it raises, as Rails would.
    static string Parse(string query)
    {
        try
        {
            return ParamBuilder.FromQueryString(query).ToJson().ToJsonString(Canonical);
        }
        catch (ParamException)
        {
            return "null";
        }
    }

    static readonly JsonSerializerOptions Canonical = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 256 };
}
