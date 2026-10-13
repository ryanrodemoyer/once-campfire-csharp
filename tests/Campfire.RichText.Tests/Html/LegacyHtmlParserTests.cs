using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RichText.Attachments;
using Campfire.RichText.Html;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Html;

/// <summary>
/// <see cref="LegacyHtmlParser"/> against <c>Nokogiri::HTML.fragment</c> in the reference image
/// (<c>Vectors/legacy_html.json</c>, written by <c>Vectors/generate_legacy_html.rb</c>): the same
/// tree, and the same OpenGraph embed details read from it.
/// </summary>
public class LegacyHtmlParserTests
{
    sealed record LegacyCase(string Input, JsonArray Tree, JsonObject Embed);

    sealed record LegacyFile(string Nokogiri, string Libxml2, IReadOnlyList<LegacyCase> Cases);

    static readonly Lazy<LegacyFile> File = new(() => VectorFiles.Parse<LegacyFile>(System.IO.File.ReadAllBytes(
        Path.Combine(VectorFiles.Root, "tests/Campfire.RichText.Tests/Html/Vectors/legacy_html.json"))));

    [Fact]
    public void Builds_the_trees_libxml2_builds()
    {
        var mismatches = File.Value.Cases
            .Select(c => (c.Input, Expected: c.Tree, Actual: Children(LegacyHtmlParser.ParseFragment(c.Input))))
            .Where(c => !JsonNode.DeepEquals(c.Expected, c.Actual))
            .Select(c => $"{JsonSerializer.Serialize(c.Input)}:\n{c.Expected.ToJsonString()}\n{c.Actual.ToJsonString()}")
            .ToList();

        Assert.True(File.Value.Cases.Count > 3000);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} trees differ from libxml2's; the first:\n{mismatches.FirstOrDefault()}");
    }

    [Fact]
    public void Reads_the_embed_details_rails_reads()
    {
        var mismatches = File.Value.Cases
            .Select(c => (c.Input, Expected: c.Embed, Actual: Embed(c.Input)))
            .Where(c => !JsonNode.DeepEquals(c.Expected, c.Actual))
            .Select(c => $"{JsonSerializer.Serialize(c.Input)}:\n{c.Expected.ToJsonString()}\n{c.Actual.ToJsonString()}")
            .ToList();

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} embeds differ from Rails'; the first:\n{mismatches.FirstOrDefault()}");
    }

    static JsonObject Embed(string input)
    {
        var (href, image, title, description) = AttachmentResolution.ContentDetails(input);
        return new JsonObject { ["href"] = href, ["image"] = image, ["title"] = title, ["description"] = description };
    }

    // generate_legacy_html.rb's shape: [name, [[attribute, value]...], children...], text as strings
    static JsonArray Children(HtmlParentNode parent)
    {
        var children = new JsonArray();
        foreach (var child in parent.Children)
        {
            switch (child)
            {
                case HtmlCharacterData text when text is not HtmlComment:
                    if (children.Count > 0 && children[^1] is JsonValue last)
                    {
                        children[^1] = last.GetValue<string>() + text.Data;
                    }
                    else
                    {
                        children.Add(text.Data);
                    }
                    break;
                case HtmlElement element:
                    var node = new JsonArray(element.Name, new JsonArray([.. element.Attributes.Select(a => new JsonArray(a.QualifiedName, a.Value))]));
                    foreach (var grandchild in Children(element))
                    {
                        node.Add(grandchild?.DeepClone());
                    }
                    children.Add(node);
                    break;
            }
        }
        return children;
    }
}
