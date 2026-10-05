namespace Campfire.Vectors;

/// <summary>One line of vectors/MANIFEST: a vector's SHA-256, its path and where it came from.</summary>
public sealed record ManifestEntry(string Sha256, string Path, string Source);

/// <summary>
/// vectors/MANIFEST, written by reference-tools/import.sh. It pins every imported vector to the
/// bytes of its source file in reference-rust at the submodule's commit.
/// </summary>
public static class Manifest
{
    static readonly Lazy<IReadOnlyList<ManifestEntry>> All = new(Read);

    public static IReadOnlyList<ManifestEntry> Entries => All.Value;

    public static TheoryData<ManifestEntry> Rows() => VectorFiles.Rows(Entries, e => e.Path);

    static List<ManifestEntry> Read() =>
        File.ReadLines(VectorFiles.PathOf("MANIFEST"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(Parse)
            .ToList();

    static ManifestEntry Parse(string line)
    {
        var fields = line.Split("  ");
        if (fields.Length != 3)
        {
            throw new InvalidDataException($"Malformed vectors/MANIFEST line: {line}");
        }
        return new ManifestEntry(fields[0], fields[1], fields[2]);
    }
}
