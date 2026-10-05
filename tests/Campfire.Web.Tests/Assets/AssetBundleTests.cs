using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Web.Assets;

namespace Campfire.Web.Tests.Assets;

// The built frontend against the reference's own `assets:precompile` and layout helpers.
public sealed partial class AssetBundleTests
{
    static AssetBundle Bundle => ReferenceAssets.Bundle;

    [Fact]
    public void Manifest_matches_the_reference_precompile()
    {
        var reference = ReferenceAssets.JsonFixture("manifest.json").EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetProperty("digested_path").GetString());
        var ours = Bundle.Manifest.ToDictionary(entry => entry.Key, entry => (string?)entry.Value);

        Assert.Equal(reference.OrderBy(e => e.Key, StringComparer.Ordinal), ours.OrderBy(e => e.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Served_manifest_has_the_reference_entries_and_length()
    {
        // The reference lists entries in its build machine's readdir order, so only the entries
        // and the byte length can match; see assets/README.md.
        var served = Bundle.Files["/assets/.manifest.json"];
        var expected = ReferenceAssets.JsonFixture("static_responses.json").EnumerateArray()
            .Single(response => response.GetProperty("path").GetString() == "/assets/.manifest.json");
        Assert.Equal(expected.GetProperty("headers").GetProperty("content-length").GetString(), served.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var ours = JsonDocument.Parse(served);
        var reference = ReferenceAssets.JsonFixture("manifest.json");
        Assert.Equal(
            reference.EnumerateObject().Select(e => $"{e.Name}={e.Value.GetRawText()}").Order(StringComparer.Ordinal),
            ours.RootElement.EnumerateObject().Select(e => $"{e.Name}={e.Value.GetRawText()}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_compiled_file_is_byte_identical_to_the_reference_precompile()
    {
        var reference = ReferenceAssets.JsonFixture("compiled_sha256.json").EnumerateObject().ToList();
        var mismatched = reference
            .Where(file => !Bundle.Files.TryGetValue($"/assets/{file.Name}", out var bytes) || ReferenceAssets.Sha256(bytes) != file.Value.GetString())
            .Select(file => file.Name)
            .ToList();

        Assert.Empty(mismatched);
        Assert.Equal(reference.Count, Bundle.Manifest.Count);
        Assert.Equal(reference.Count + 1, Bundle.Files.Keys.Count(url => url.StartsWith("/assets/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Importmap_tags_match_the_reference_helper()
    {
        Assert.Equal(ReferenceAssets.Fixture("javascript_importmap_tags.html"), Bundle.ImportmapTags);
    }

    [Fact]
    public void Importmap_json_is_the_json_inside_the_reference_tag()
    {
        var tags = ReferenceAssets.Fixture("javascript_importmap_tags.html");
        var json = ImportmapScript().Match(tags).Groups[1].Value;

        Assert.Equal(json, Bundle.ImportmapJson);
    }

    [Fact]
    public void Stylesheet_paths_are_those_stylesheet_link_tag_all_links()
    {
        var hrefs = Href().Matches(ReferenceAssets.Fixture("stylesheet_link_tag_all.html")).Select(match => match.Groups[1].Value);

        Assert.Equal(hrefs, Bundle.StylesheetPaths.Select(Bundle.AssetPath));
    }

    [Fact]
    public void Asset_path_resolves_like_action_view()
    {
        Assert.Equal("/assets/56k-67359aa6.mp3", Bundle.AssetPath("56k.mp3"));
        Assert.Equal("/assets/56k-67359aa6.mp3?v=1#t", Bundle.AssetPath("56k.mp3?v=1#t"));
        Assert.Equal("/robots.txt", Bundle.AssetPath("/robots.txt"));
        Assert.Equal("https://example.com/a.js", Bundle.AssetPath("https://example.com/a.js"));
        Assert.Equal("", Bundle.AssetPath(" "));
        Assert.Null(Bundle.AssetPath("missing.png"));
    }

    [Fact]
    public void A_written_bundle_loads_back_the_same()
    {
        var directory = Directory.CreateTempSubdirectory("campfire-assets-");
        try
        {
            Bundle.WriteTo(directory.FullName);
            var loaded = AssetBundle.Load(directory.FullName);

            Assert.Equal(Bundle.Files.Keys, loaded.Files.Keys);
            Assert.All(Bundle.Files, file => Assert.Equal(file.Value, loaded.Files[file.Key]));
            Assert.Equal(Bundle.Manifest, loaded.Manifest);
            Assert.Equal(Bundle.StylesheetPaths, loaded.StylesheetPaths);
            Assert.Equal(Bundle.ImportmapJson, loaded.ImportmapJson);
            Assert.Equal(Bundle.ImportmapTags, loaded.ImportmapTags);
            Assert.Equal(Bundle.LastModified, loaded.LastModified);
            Assert.Equal(Bundle.ImportmapJson, File.ReadAllText(Path.Combine(directory.FullName, "importmap.json"), Encoding.UTF8));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_port_overrides_nothing_yet()
    {
        // Overrides change digests and bytes on purpose. Adding one means listing it in
        // assets/README.md and teaching these golden tests which files may differ.
        var overrides = Path.Combine(ReferenceAssets.RepositoryRoot, "assets", "overrides");
        Assert.DoesNotContain(Directory.GetFiles(overrides, "*", SearchOption.AllDirectories), file => !Path.GetFileName(file).StartsWith('.'));
    }

    [GeneratedRegex("""^<script type="importmap" data-turbo-track="reload">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex ImportmapScript();

    [GeneratedRegex("""href="([^"]+)" """)]
    private static partial Regex Href();
}
