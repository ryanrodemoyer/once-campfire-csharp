using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Web.Assets;

// One file on the load path: its logical path ("controllers/foo.js") and its bytes.
sealed record PropshaftAsset(string LogicalPath, string SourcePath, byte[] Content);

// A port of Propshaft 1.2.1's LoadPath, Asset digesting and compilers (lib/propshaft/*), so digested
// paths and compiled bytes match `bin/rails assets:precompile`.
//
// Propshaft reads assets as ASCII-8BIT and its patterns match bytes. Here content is decoded as
// Latin-1, one char per byte, so .NET's regex sees exactly those bytes and encoding back is lossless.
sealed partial class PropshaftLoadPath
{
    // config.assets.prefix, Propshaft's default, which the app keeps. Compiler#url_prefix also
    // prepends relative_url_root, which the app doesn't set.
    public const string UrlPrefix = "/assets";

    readonly Dictionary<string, PropshaftAsset> byLogicalPath = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> digestedPaths = new(StringComparer.Ordinal);
    readonly string version;

    public PropshaftLoadPath(IEnumerable<string> paths, string version)
    {
        this.version = version;

        // LoadPath#assets_by_path: earlier paths win for a logical path; dotfiles are skipped.
        foreach (var path in Dedup(paths.ToList()))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in FilesFromTree(path))
            {
                if (Path.GetFileName(file).StartsWith('.'))
                {
                    continue;
                }

                var logicalPath = Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!byLogicalPath.ContainsKey(logicalPath))
                {
                    var asset = new PropshaftAsset(logicalPath, file, File.ReadAllBytes(file));
                    byLogicalPath.Add(logicalPath, asset);
                    Assets.Add(asset);
                }
            }
        }
    }

    // In load path order. Propshaft walks each directory in readdir order, which differs between
    // filesystems; here each directory is walked in ordinal order so builds are reproducible.
    public List<PropshaftAsset> Assets { get; } = [];

    public PropshaftAsset? Find(string logicalPath) => byLogicalPath.GetValueOrDefault(logicalPath);

    // Asset#digested_path
    public string DigestedPath(PropshaftAsset asset)
    {
        if (digestedPaths.TryGetValue(asset.LogicalPath, out var cached))
        {
            return cached;
        }

        var digested = AlreadyDigested().IsMatch(asset.LogicalPath)
            ? asset.LogicalPath
            : DigestableExtension().Replace(asset.LogicalPath, extension => $"-{Digest(asset)}{extension.Value}", 1);
        digestedPaths[asset.LogicalPath] = digested;
        return digested;
    }

    // Asset#digest: SHA1 of the content, then the content of everything it references (in discovery
    // order), then the assets version; the first 8 hex digits.
    public string Digest(PropshaftAsset asset)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(asset.Content);
        foreach (var referenced in ReferencedBy(asset))
        {
            sha1.AppendData(referenced.Content);
        }

        sha1.AppendData(Encoding.UTF8.GetBytes(version));
        return Convert.ToHexStringLower(sha1.GetHashAndReset())[..8];
    }

    // Compilers#compile: null when no compiler is registered for the asset's type, so the file is
    // copied as is. text/css runs CssAssetUrls then SourceMappingUrls; text/javascript runs
    // JsAssetUrls then SourceMappingUrls.
    public byte[]? CompiledContent(PropshaftAsset asset)
    {
        if (AssetUrlPattern(asset) is not { } pattern)
        {
            return null;
        }

        var input = Encoding.Latin1.GetString(asset.Content);
        var output = CompileSourceMappingUrls(asset, CompileAssetUrls(asset, pattern, input));
        return Encoding.Latin1.GetBytes(output);
    }

    // Asset#content_type, reduced to the two types Propshaft registers compilers for.
    static Regex? AssetUrlPattern(PropshaftAsset asset) => RubyPath.Extname(asset.LogicalPath) switch
    {
        ".css" => CssAssetUrls(),
        ".js" => JsAssetUrls(),
        _ => null,
    };

    // LoadPath#find_referenced_by: CssAssetUrls#referenced_by / JsAssetUrls#referenced_by, minus the
    // asset itself. Referenced files are scanned with the referencing asset's pattern whatever their
    // own type.
    List<PropshaftAsset> ReferencedBy(PropshaftAsset asset)
    {
        var references = new List<PropshaftAsset>();
        if (AssetUrlPattern(asset) is { } pattern)
        {
            CollectReferences(asset, pattern, references);
        }

        references.Remove(asset);
        return references;
    }

    void CollectReferences(PropshaftAsset asset, Regex pattern, List<PropshaftAsset> references)
    {
        var directory = RubyPath.Dirname(asset.LogicalPath);
        foreach (Match match in pattern.Matches(Encoding.Latin1.GetString(asset.Content)))
        {
            if (Find(ResolvePath(directory, Utf8(match.Groups[1].Value))) is { } referenced && !references.Contains(referenced))
            {
                references.Add(referenced);
                CollectReferences(referenced, pattern, references);
            }
        }
    }

    // CssAssetUrls#compile / JsAssetUrls#compile
    string CompileAssetUrls(PropshaftAsset asset, Regex pattern, string input)
    {
        var directory = RubyPath.Dirname(asset.LogicalPath);
        var isCss = RubyPath.Extname(asset.LogicalPath) == ".css";
        return pattern.Replace(input, match =>
        {
            var url = match.Groups[1].Value;
            var fingerprint = match.Groups[2].Value;
            var quoted = Find(ResolvePath(directory, Utf8(url))) is { } found
                ? $"\"{UrlPrefix}/{Latin1(DigestedPath(found))}{fingerprint}\""
                : $"\"{url}\"";
            return isCss ? $"url({quoted})" : quoted;
        });
    }

    // SourceMappingUrls#compile: points the comment at the digested map, or drops the comment's URL
    // when the map isn't on the load path.
    string CompileSourceMappingUrls(PropshaftAsset asset, string input)
    {
        var directory = RubyPath.Dirname(asset.LogicalPath);
        return SourceMappingUrls().Replace(input, match =>
        {
            var commentStart = match.Groups[1].Value;
            var commentEnd = match.Groups[3].Value;
            var url = PrefixInSourceMapUrl().Replace(Utf8(match.Groups[2].Value), "");
            var resolved = directory == "." ? url : RubyPath.Plus(directory, url);
            return Find(resolved) is { } found
                ? $"{commentStart}# sourceMappingURL={UrlPrefix}/{Latin1(DigestedPath(found))}{commentEnd}"
                : commentStart + commentEnd;
        });
    }

    // CssAssetUrls#resolve_path (JsAssetUrls has the same one).
    static string ResolvePath(string directory, string filename)
    {
        if (filename.StartsWith("../", StringComparison.Ordinal))
        {
            return RubyPath.Cleanpath(RubyPath.Plus(directory, filename));
        }

        if (filename.StartsWith('/'))
        {
            return filename[1..];
        }

        return RubyPath.Plus(directory, filename.StartsWith("./", StringComparison.Ordinal) ? filename[2..] : filename);
    }

    // LoadPath#dedup: drops a path that string-starts with an earlier one in sorted order, keeping
    // the original order. Pathname sorts as if "/" were "\0".
    static List<string> Dedup(List<string> paths)
    {
        var deduped = new List<string>();
        foreach (var path in paths.Order(Comparer<string>.Create((a, b) => string.CompareOrdinal(a.Replace('/', '\0'), b.Replace('/', '\0')))))
        {
            if (deduped.Count == 0 || !path.StartsWith(deduped[^1], StringComparison.Ordinal))
            {
                deduped.Add(path);
            }
        }

        return paths.Distinct(StringComparer.Ordinal).Where(path => deduped.Contains(path, StringComparer.Ordinal)).ToList();
    }

    static List<string> FilesFromTree(string directory)
    {
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToList();
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    // A captured URL as the UTF-8 string it spells (paths on the load path are UTF-8).
    static string Utf8(string latin1) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(latin1));

    // A UTF-8 string as the Latin-1 chars of its bytes, for splicing into decoded content.
    static string Latin1(string utf8) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(utf8));

    // Ruby's \s is [ \t\n\v\f\r]; spelled out, since .NET's \s also matches \x85 and \xA0.
    const string space = @"[ \t\n\x0B\f\r]";
    const string notUrl = @"[^""' \t\n\x0B\f\r?#)]";

    // Propshaft::Compiler::CssAssetUrls::ASSET_URL_PATTERN
    [GeneratedRegex(@"url\(" + space + @"*[""']?(?!(?:\#|%23|data:|http:|https:|//))(" + notUrl + @"+)([#?][^""')]+)?" + space + @"*[""']?\)")]
    private static partial Regex CssAssetUrls();

    // Propshaft::Compiler::JsAssetUrls::ASSET_URL_PATTERN
    [GeneratedRegex(@"RAILS_ASSET_URL\(" + space + @"*[""']?(?!(?:\#|%23|data|http|//))(" + notUrl + @"+)([#?][^""')]+)?" + space + @"*[""']?\)")]
    private static partial Regex JsAssetUrls();

    // Propshaft::Compiler::SourceMappingUrls::SOURCE_MAPPING_PATTERN. Ruby's . and \Z mean the same
    // as .NET's: anything but \n, and the end or just before a final \n.
    [GeneratedRegex(@"(//|/\*)# sourceMappingURL=(.+\.map)(" + space + @"*?\*/)?" + space + @"*?\Z")]
    private static partial Regex SourceMappingUrls();

    // SourceMappingUrls#asset_path: /^(.+\/)?#{url_prefix}\// where ^ is any line start.
    [GeneratedRegex(@"^(.+/)?/assets/", RegexOptions.Multiline)]
    private static partial Regex PrefixInSourceMapUrl();

    // Asset#already_digested?
    [GeneratedRegex(@"-([0-9a-zA-Z_-]{7,128})\.digested")]
    private static partial Regex AlreadyDigested();

    // Asset#digested_path: logical_path.sub(/\.(\w+(\.map)?)$/). Ruby's \w is ASCII-only here, and
    // $ is any line end.
    [GeneratedRegex(@"\.([a-zA-Z0-9_]+(\.map)?)$", RegexOptions.Multiline)]
    private static partial Regex DigestableExtension();
}
