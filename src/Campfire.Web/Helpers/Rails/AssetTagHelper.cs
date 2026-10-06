using System.Text;
using System.Text.RegularExpressions;
using Campfire.Web.Routing;

namespace Campfire.Web.Helpers;

// ActionView::Helpers::AssetUrlHelper and AssetTagHelper over Propshaft's manifest, with the
// reference's settings: no asset host, no relative_url_root, preload_links_header on,
// apply_stylesheet_media_default off, no image_loading/image_decoding defaults.
// reference: actionview/lib/action_view/helpers/asset_url_helper.rb, asset_tag_helper.rb,
// propshaft/lib/propshaft/helper.rb
public partial class View
{
    // AssetTagHelper::MAX_HEADER_SIZE.
    const int maxPreloadHeaderSize = 1_000;

    readonly List<string> preloadLinks = [];

    /// <summary>
    /// The <c>link</c> response header <c>stylesheet_link_tag</c> builds
    /// (<c>send_preload_links_header</c>), or null when nothing was preloaded. The caller sets it
    /// on the response.
    /// </summary>
    public string? LinkHeader
    {
        get
        {
            if (preloadLinks.Count == 0)
            {
                return null;
            }

            var header = new StringBuilder();
            foreach (var link in preloadLinks)
            {
                if (Encoding.UTF8.GetByteCount(header.ToString()) + Encoding.UTF8.GetByteCount(link) > maxPreloadHeaderSize)
                {
                    break;
                }
                if (header.Length > 0)
                {
                    header.Append(',');
                }
                header.Append(link);
            }
            return header.ToString();
        }
    }

    /// <summary>
    /// <c>asset_path(source)</c>. A logical path missing from the manifest throws, as Propshaft's
    /// <c>MissingAssetError</c> does.
    /// </summary>
    public string AssetPath(string source, string? extname = null)
    {
        if (RubyValues.IsBlank(source))
        {
            return "";
        }
        if (UriPattern().IsMatch(source))
        {
            return source;
        }

        var tail = TailPattern().Match(source) is { Success: true } match ? match.Value : "";
        source = TailPattern().Replace(source, "", 1);
        if (extname is not null && Extname(source) != extname)
        {
            source += extname;
        }

        if (!source.StartsWith('/'))
        {
            source = Assets.AssetPath(source) ?? throw new MissingAssetException(source);
        }
        return source + tail;
    }

    /// <summary><c>image_path(source)</c>.</summary>
    public string ImagePath(string source) => AssetPath(source);

    /// <summary><c>image_url(source)</c>: the path on <see cref="Origin"/>.</summary>
    public string ImageUrl(string source) => AssetUrl(source);

    /// <summary><c>asset_url(source)</c>: <c>request.base_url</c> in front of the path.</summary>
    public string AssetUrl(string source)
    {
        var path = AssetPath(source);
        return UriPattern().IsMatch(path) ? path : RubyPathJoin(BaseUrl, path);
    }

    /// <summary><c>stylesheet_path(source)</c>.</summary>
    public string StylesheetPath(string source) => AssetPath(source, ".css");

    /// <summary>
    /// <c>image_tag(source, options)</c>: <c>src</c> goes after the given options, <c>size</c>
    /// ("24" or "24x16") becomes <c>width</c> and <c>height</c> at the end.
    /// </summary>
    public SafeString ImageTag(string source, HtmlOptions? options = null)
    {
        options = options?.Clone() ?? [];
        if (options.ContainsKey("size") && (options.ContainsKey("height") || options.ContainsKey("width")))
        {
            throw new ArgumentException("Cannot pass a :size option with a :height or :width option");
        }

        options.Delete("skip_pipeline");
        options["src"] = AssetPath(source);
        if (TagHelper.IsTruthy(options["size"]))
        {
            var (width, height) = ExtractDimensions(RubyValues.ToS(options.Delete("size")));
            options["width"] = width;
            options["height"] = height;
        }
        return TagHelper.Tag("img", options);
    }

    /// <summary>
    /// <c>stylesheet_link_tag :all, options</c>: a link tag for every stylesheet in the manifest,
    /// joined by newlines, each recorded for <see cref="LinkHeader"/>.
    /// </summary>
    public SafeString StylesheetLinkTagAll(HtmlOptions? options = null) => StylesheetLinkTag(Assets.StylesheetPaths, options);

    /// <summary><c>stylesheet_link_tag(*sources, options)</c>.</summary>
    public SafeString StylesheetLinkTag(IEnumerable<string> sources, HtmlOptions? options = null)
    {
        options = options?.Clone() ?? [];
        options.Delete("integrity");
        foreach (var key in new[] { "protocol", "extname", "host", "skip_pipeline" })
        {
            options.Delete(key);
        }
        var usePreloadLinksHeader = options.ContainsKey("preload_links_header")
            ? TagHelper.IsTruthy(options.Delete("preload_links_header"))
            : true;
        var crossorigin = options.Delete("crossorigin");
        if (crossorigin is true)
        {
            crossorigin = "anonymous";
        }
        var nopush = !options.ContainsKey("nopush") || TagHelper.IsTruthy(options.Delete("nopush"));

        var tags = new List<string>();
        foreach (var source in sources.Distinct(StringComparer.Ordinal))
        {
            var href = StylesheetPath(source);
            if (usePreloadLinksHeader && RubyValues.IsPresent(href) && !href.StartsWith("data:", StringComparison.Ordinal))
            {
                var link = $"<{href}>; rel=preload; as=style";
                if (crossorigin is not null)
                {
                    link += $"; crossorigin={RubyValues.ToS(crossorigin)}";
                }
                if (nopush)
                {
                    link += "; nopush";
                }
                preloadLinks.Add(link);
            }

            var tagOptions = new HtmlOptions { { "rel", "stylesheet" }, { "crossorigin", crossorigin }, { "href", href } }.Merge(options);
            tags.Add(TagHelper.Tag("link", tagOptions).Value);
        }
        return new SafeString(string.Join("\n", tags));
    }

    /// <summary><c>javascript_importmap_tags</c> for the reference's importmap.</summary>
    public SafeString JavascriptImportmapTags() => new(Assets.ImportmapTags);

    // request.base_url.
    string BaseUrl => UrlGenerator.HostUrl(Origin);

    static (string? Width, string? Height) ExtractDimensions(string size)
    {
        if (SizeWithHeight().IsMatch(size))
        {
            var parts = size.Split('x');
            return (parts[0], parts[1]);
        }
        return SizeAlone().IsMatch(size) ? (size, size) : (null, null);
    }

    // File.extname: the basename's last extension, ignoring a leading dot.
    static string Extname(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : "";
    }

    // File.join(host, path): one slash between the two.
    static string RubyPathJoin(string left, string right) => left.TrimEnd('/') + "/" + right.TrimStart('/');

    [GeneratedRegex(@"^[-a-z]+://|^(?:cid|data):|^//", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UriPattern();

    [GeneratedRegex(@"([?#].+)$", RegexOptions.Multiline)]
    private static partial Regex TailPattern();

    [GeneratedRegex(@"\A\d+(?:\.\d+)?x\d+(?:\.\d+)?\z")]
    private static partial Regex SizeWithHeight();

    [GeneratedRegex(@"\A\d+(?:\.\d+)?\z")]
    private static partial Regex SizeAlone();
}

/// <summary>Propshaft's <c>MissingAssetError</c>: the logical path isn't in the manifest.</summary>
public sealed class MissingAssetException(string path) : Exception($"The asset '{path}' was not found in the load path.");
