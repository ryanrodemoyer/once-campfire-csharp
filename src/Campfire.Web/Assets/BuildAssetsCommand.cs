using System.Globalization;

namespace Campfire.Web.Assets;

// bin/build-assets: builds the AssetBundle from a checkout and writes it to a directory.
public static class BuildAssetsCommand
{
    // args: the repository root, then the output directory (relative to the root). The build time,
    // which becomes every file's last-modified, is $SOURCE_DATE_EPOCH when set, so builds can be
    // reproduced byte for byte.
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args is not [var root, var outputDirectory])
        {
            output.WriteLine("usage: bin/build-assets [output directory, default artifacts/assets]");
            return 64;
        }

        var directory = Path.GetFullPath(outputDirectory, root);
        var bundle = AssetBundle.Build(AssetSources.InRepository(root), BuildTime());
        bundle.WriteTo(directory);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Wrote {bundle.Manifest.Count} assets, {bundle.Files.Count} public files and the import map to {directory}"));
        return 0;
    }

    static DateTimeOffset BuildTime()
    {
        var epoch = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        return long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
