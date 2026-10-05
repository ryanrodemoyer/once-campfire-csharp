using System.Text.Json;

namespace Campfire.Vectors;

/// <summary>Finds and parses the files under the repository's vectors/ directory.</summary>
public static class VectorFiles
{
    static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    // Every property of a vector must be declared on its record, and every non-nullable one must
    // be present and non-null, so a regenerated vector that changes shape fails to load.
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static string Root => RepositoryRoot.Value;

    public static string VectorsPath => Path.Combine(Root, "vectors");

    public static string PathOf(string relativePath) => Path.Combine(VectorsPath, relativePath);

    public static byte[] ReadBytes(string relativePath) => File.ReadAllBytes(PathOf(relativePath));

    public static T Load<T>(string relativePath) => Parse<T>(File.ReadAllBytes(PathOf(relativePath)));

    /// <summary>Parses vector JSON with the same strict rules <see cref="Load{T}"/> uses.</summary>
    public static T Parse<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Vector JSON is null");

    /// <summary>Wraps each case in a theory row named after it, for <c>[MemberData]</c>.</summary>
    public static TheoryData<T> Rows<T>(IEnumerable<T> cases, Func<T, string> name) =>
        new(cases.Select(c => new TheoryDataRow<T>(c) { TestDisplayName = name(c) }));

    /// <summary>Wraps each case in a theory row numbered by its position in the file.</summary>
    public static TheoryData<T> Rows<T>(IEnumerable<T> cases) =>
        new(cases.Select((c, i) => new TheoryDataRow<T>(c) { TestDisplayName = $"{typeof(T).Name} #{i}" }));

    static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Campfire.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException($"No Campfire.slnx above {AppContext.BaseDirectory}");
    }
}
