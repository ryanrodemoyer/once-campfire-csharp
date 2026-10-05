using System.Text.RegularExpressions;

namespace Campfire.Web.Tests.Assets;

// assets/vendor holds the gem-provided assets byte for byte, at the versions reference/Gemfile.lock
// resolves; vendor/MANIFEST.md records each file's gem, version and SHA-256.
public sealed partial class VendorTests
{
    static readonly string Vendor = Path.Combine(ReferenceAssets.RepositoryRoot, "assets", "vendor");

    static List<(string Gem, string Version, string Path, string Sha256)> ManifestRows() =>
        File.ReadAllLines(Path.Combine(Vendor, "MANIFEST.md"))
            .Select(line => ManifestRow().Match(line))
            .Where(match => match.Success)
            .Select(match => (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value))
            .ToList();

    [Fact]
    public void Every_vendored_file_matches_its_recorded_sha256()
    {
        var rows = ManifestRows();
        Assert.NotEmpty(rows);

        foreach (var (gem, _, path, sha256) in rows)
        {
            var file = Path.Combine(Vendor, gem, path);
            Assert.Equal((file, sha256), (file, ReferenceAssets.Sha256(File.ReadAllBytes(file))));
        }
    }

    [Fact]
    public void Vendored_gems_are_the_versions_the_reference_locks()
    {
        var locked = File.ReadAllLines(Path.Combine(ReferenceAssets.RepositoryRoot, "reference", "Gemfile.lock"))
            .Select(line => LockedSpec().Match(line))
            .Where(match => match.Success)
            .GroupBy(match => match.Groups[1].Value)
            .ToDictionary(group => group.Key, group => group.First().Groups[2].Value);

        foreach (var (gem, version, _, _) in ManifestRows())
        {
            Assert.Equal((gem, version), (gem, locked.GetValueOrDefault(gem)));
        }
    }

    // | gem | version | source | path | sha256 |
    [GeneratedRegex(@"^\| ([^ |]+) \| ([^ |]+) \| [^|]+ \| ([^ |]+) \| ([0-9a-f]{64}) \|$")]
    private static partial Regex ManifestRow();

    // A resolved spec in Gemfile.lock: four spaces, the name, the version in parentheses.
    [GeneratedRegex(@"^    ([^ ]+) \(([^)]+)\)$")]
    private static partial Regex LockedSpec();
}
