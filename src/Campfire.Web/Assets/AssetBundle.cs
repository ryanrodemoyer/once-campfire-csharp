using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Campfire.Web.Assets;

// The reference's frontend as `bin/rails assets:precompile` leaves it: every file
// ActionDispatch::Static serves (reference/public plus public/assets with its digested files and
// .manifest.json), the Propshaft manifest, and the rendered import map.
//
// bin/build-assets builds one and writes it to a directory; the server loads that directory.
public sealed partial class AssetBundle
{
    const string manifestUrl = PropshaftLoadPath.UrlPrefix + "/.manifest.json";
    const string publicDirectory = "public";
    const string importmapJsonFile = "importmap.json";
    const string importmapTagsFile = "importmap_tags.html";

    readonly Dictionary<string, string> digestedPaths;

    AssetBundle(
        SortedDictionary<string, byte[]> files,
        List<KeyValuePair<string, string>> manifest,
        string importmapJson,
        string importmapTags,
        DateTimeOffset lastModified)
    {
        Files = files;
        Manifest = manifest;
        digestedPaths = new Dictionary<string, string>(manifest, StringComparer.Ordinal);
        ImportmapJson = importmapJson;
        ImportmapTags = importmapTags;
        LastModified = lastModified;

        // Propshaft::Helper#all_stylesheets_paths: the logical path of every text/css asset, sorted.
        StylesheetPaths = manifest
            .Select(entry => entry.Key)
            .Where(logicalPath => RubyPath.Extname(logicalPath) == ".css")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // URL path ("/robots.txt", "/assets/application-a54c74a7.js") to bytes, in ordinal order.
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    // (logical path, digested path) in Propshaft's load path order, as .manifest.json lists them.
    public IReadOnlyList<KeyValuePair<string, string>> Manifest { get; }

    public IReadOnlyList<string> StylesheetPaths { get; }

    // Importmap::Map#to_json for the reference's config/importmap.rb.
    public string ImportmapJson { get; }

    // javascript_importmap_tags, as the application layout renders it.
    public string ImportmapTags { get; }

    // Rack::Files' last-modified for every file: when the bundle was built.
    public DateTimeOffset LastModified { get; }

    public string? DigestedPath(string logicalPath) => digestedPaths.GetValueOrDefault(logicalPath);

    // ActionView's asset_path over the precompiled manifest (Propshaft::Resolver::Static), for a
    // source without an extension option: URLs and absolute paths pass through, a ?query or
    // #fragment is kept, and a missing asset is null where Propshaft raises MissingAssetError.
    public string? AssetPath(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "";
        }

        if (UriPattern().IsMatch(source))
        {
            return source;
        }

        var tail = TailPattern().Match(source).Value;
        source = TailPattern().Replace(source, "", 1);
        if (!source.StartsWith('/'))
        {
            if (DigestedPath(source) is not { } digested)
            {
                return null;
            }

            source = RubyPath.Join(PropshaftLoadPath.UrlPrefix, digested);
        }

        return source + tail;
    }

    // Digests and compiles every asset on the load path, renders the manifest and the import map,
    // and gathers reference/public: all of `assets:precompile`, without Ruby or Node.
    public static AssetBundle Build(AssetSources sources, DateTimeOffset builtAt)
    {
        var loadPath = new PropshaftLoadPath(sources.LoadPath(), sources.AssetsVersion());
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        // ActionDispatch::Static serves reference/public; a stray precompile in the submodule
        // mustn't shadow what is built here.
        var publicRoot = Path.Combine(sources.RailsRoot, "public");
        if (Directory.Exists(publicRoot))
        {
            foreach (var file in Directory.GetFiles(publicRoot, "*", SearchOption.AllDirectories))
            {
                var url = "/" + Path.GetRelativePath(publicRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!url.StartsWith(PropshaftLoadPath.UrlPrefix + "/", StringComparison.Ordinal))
                {
                    files[url] = File.ReadAllBytes(file);
                }
            }
        }

        // Propshaft::Processor#output_assets
        var manifest = new List<KeyValuePair<string, string>>();
        foreach (var asset in loadPath.Assets)
        {
            var digested = loadPath.DigestedPath(asset);
            manifest.Add(new(asset.LogicalPath, digested));
            files[$"{PropshaftLoadPath.UrlPrefix}/{digested}"] = loadPath.CompiledContent(asset) ?? asset.Content;
        }

        files[manifestUrl] = Encoding.UTF8.GetBytes(ManifestJson(manifest));

        var pins = Importmap.Expand(Path.Combine(sources.RailsRoot, "config", "importmap.rb"), sources.RailsRoot);
        var resolver = new AssetBundle(files, manifest, "", "", builtAt);
        return new AssetBundle(
            files,
            manifest,
            Importmap.Json(pins, resolver.AssetPath),
            Importmap.Tags(pins, resolver.AssetPath),
            builtAt);
    }

    // Writes public/ (what ActionDispatch::Static serves), importmap.json and importmap_tags.html.
    // Every file's mtime is the build time, which Load reads back as LastModified.
    public void WriteTo(string directory)
    {
        var publicRoot = Path.Combine(directory, publicDirectory);
        if (Directory.Exists(publicRoot))
        {
            Directory.Delete(publicRoot, recursive: true);
        }

        foreach (var (url, bytes) in Files)
        {
            Write(Path.Combine(publicRoot, url.TrimStart('/')), bytes);
        }

        Write(Path.Combine(directory, importmapJsonFile), Encoding.UTF8.GetBytes(ImportmapJson));
        Write(Path.Combine(directory, importmapTagsFile), Encoding.UTF8.GetBytes(ImportmapTags));

        void Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, LastModified.UtcDateTime);
        }
    }

    // Reads back a directory written by WriteTo (bin/build-assets).
    public static AssetBundle Load(string directory)
    {
        var publicRoot = Path.Combine(directory, publicDirectory);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(publicRoot, "*", SearchOption.AllDirectories))
        {
            files["/" + Path.GetRelativePath(publicRoot, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(file);
        }

        var manifest = new List<KeyValuePair<string, string>>();
        using (var json = JsonDocument.Parse(files[manifestUrl]))
        {
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                manifest.Add(new(entry.Name, entry.Value.GetProperty("digested_path").GetString()!));
            }
        }

        var manifestFile = Path.Combine(publicRoot, manifestUrl.TrimStart('/'));
        return new AssetBundle(
            files,
            manifest,
            File.ReadAllText(Path.Combine(directory, importmapJsonFile)),
            File.ReadAllText(Path.Combine(directory, importmapTagsFile)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(manifestFile), TimeSpan.Zero));
    }

    // Propshaft::Manifest#to_json (ActiveSupport's): {"logical":{"digested_path":"...","integrity":null}}.
    // No integrity hash algorithm is configured.
    static string ManifestJson(List<KeyValuePair<string, string>> manifest) =>
        "{" + string.Join(",", manifest.Select(entry => string.Create(CultureInfo.InvariantCulture,
            $"{RubyJson.ActiveSupportString(entry.Key)}:{{\"digested_path\":{RubyJson.ActiveSupportString(entry.Value)},\"integrity\":null}}"))) + "}";

    // ActionView::Helpers::AssetUrlHelper::URI_REGEXP
    [GeneratedRegex("^[-a-z]+://|^(?:cid|data):|^//", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UriPattern();

    // AssetUrlHelper#asset_path: source[/([?#].+)$/]
    [GeneratedRegex("([?#].+)$", RegexOptions.Multiline)]
    private static partial Regex TailPattern();
}
