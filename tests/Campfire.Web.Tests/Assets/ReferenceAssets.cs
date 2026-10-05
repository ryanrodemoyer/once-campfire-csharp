using System.Security.Cryptography;
using System.Text.Json;
using Campfire.Web.Assets;

namespace Campfire.Web.Tests.Assets;

// The bundle built from this checkout, and the golden output the reference app produced for the
// same sources: `assets:precompile` and the real Rails helpers, exported by
// reference-rust/crates/assets/script/export_reference.rb into its tests/reference/.
static class ReferenceAssets
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    static readonly Lazy<AssetBundle> LazyBundle = new(() =>
        AssetBundle.Build(AssetSources.InRepository(RepositoryRoot), new DateTimeOffset(2026, 9, 26, 12, 23, 14, TimeSpan.Zero)));

    public static AssetBundle Bundle => LazyBundle.Value;

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "reference-rust", "crates", "assets", "tests", "reference", name));

    public static JsonElement JsonFixture(string name)
    {
        using var document = JsonDocument.Parse(Fixture(name));
        return document.RootElement.Clone();
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Campfire.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Campfire.slnx not found above the test output directory");
    }
}
