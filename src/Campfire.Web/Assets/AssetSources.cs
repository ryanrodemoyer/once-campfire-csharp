namespace Campfire.Web.Assets;

// Where the frontend comes from: the Rails app (reference/), the gem assets vendored from its
// Gemfile.lock (assets/vendor/), and the port's own overrides (assets/overrides/).
public sealed record AssetSources(string RailsRoot, string VendorDirectory, string OverridesDirectory)
{
    // The sources in a checkout of this repository.
    public static AssetSources InRepository(string repositoryRoot) => new(
        Path.Combine(repositoryRoot, "reference"),
        Path.Combine(repositoryRoot, "assets", "vendor"),
        Path.Combine(repositoryRoot, "assets", "overrides"));

    // Propshaft's load path: the overrides first, so they shadow files of the same logical path,
    // then the reference's own load path in Propshaft's order, as vendor/LOAD_PATH records it
    // (`reference:<dir>` under the Rails root, `vendor:<dir>` under assets/vendor).
    public IReadOnlyList<string> LoadPath()
    {
        var paths = new List<string> { OverridesDirectory };
        foreach (var line in File.ReadAllLines(Path.Combine(VendorDirectory, "LOAD_PATH")).Where(line => line.Trim().Length > 0))
        {
            paths.Add(line.Split(':', 2) switch
            {
                ["reference", var directory] => Path.Combine(RailsRoot, directory),
                ["vendor", var directory] => Path.Combine(VendorDirectory, directory),
                _ => throw new FormatException($"assets/vendor/LOAD_PATH: bad line {line}"),
            });
        }

        return paths;
    }

    // config/initializers/assets.rb sets Rails.application.config.assets.version; Propshaft's
    // default is "1".
    public string AssetsVersion()
    {
        var initializer = Path.Combine(RailsRoot, "config", "initializers", "assets.rb");
        if (!File.Exists(initializer))
        {
            return "1";
        }

        foreach (var line in File.ReadAllLines(initializer))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.Split("config.assets.version", 2) is not [_, var assignment])
            {
                continue;
            }

            assignment = assignment.Trim();
            if (assignment.StartsWith('='))
            {
                return assignment[1..].Trim().Trim('"', '\'');
            }
        }

        return "1";
    }
}
