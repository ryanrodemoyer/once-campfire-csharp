using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetVips;

namespace Campfire.Storage.Media;

/// <summary>
/// Every NetVips call, kept out of other classes so NetVips (which starts libvips when its
/// assembly is first touched) is only reached after <see cref="LibVips.EnsureInitialized"/> has
/// prepared the environment. Callers check that first.
/// </summary>
static partial class VipsImaging
{
    [GeneratedRegex("Right-top|Left-bottom|Top-right|Bottom-left", RegexOptions.CultureInvariant)]
    private static partial Regex Rotations();

    /// <summary>NetVips' start of libvips: null when it loaded, else why not.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Exception? Start() =>
        ModuleInitializer.VipsInitialized ? null : ModuleInitializer.Exception ?? new DllNotFoundException("libvips");

    /// <summary><c>vips-modules-x.y</c> for the running libvips.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ModuleDirectoryName() => $"vips-modules-{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}";

    /// <summary>reference/config/initializers/vips.rb, once every loader is registered.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Restrict()
    {
        NetVips.NetVips.BlockUntrusted = true;
        Operation.Block("VipsForeignLoadOpenslide", true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Version() => $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}";

    /// <summary>See <see cref="ImageTransformer.Transform"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Transform(string input, IReadOnlyList<(int Width, int Height)> operations, string output)
    {
        try
        {
            var image = Load(input);
            try
            {
                foreach (var (width, height) in operations)
                {
                    var resized = ResizeToLimit(image, width, height);
                    image.Dispose();
                    image = resized;
                }
                image.WriteToFile(output);
            }
            finally
            {
                image.Dispose();
            }
        }
        catch (VipsException exception)
        {
            throw new MediaException(exception.Message, exception);
        }
    }

    /// <summary>See <see cref="BlobAnalyzer.ImageMetadata"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JsonObject ImageMetadata(string path)
    {
        try
        {
            using var image = Image.NewFromFile(path, access: Enums.Access.Sequential);
            var (width, height) = IsRotated(image) ? (image.Height, image.Width) : (image.Width, image.Height);
            return new JsonObject { ["width"] = width, ["height"] = height };
        }
        catch (VipsException)
        {
            return [];
        }
    }

    /// <summary><c>ROTATIONS === image.get("exif-ifd0-Orientation")</c>, false when it's missing.</summary>
    static bool IsRotated(Image image) =>
        image.Contains("exif-ifd0-Orientation") && image.Get("exif-ifd0-Orientation") is string orientation && Rotations().IsMatch(orientation);

    /// <summary>
    /// <c>Processor.load_image(path, page: 0)</c>: <c>page</c> reaches only loaders that take it
    /// (<c>Utils.select_valid_loader_options</c>), then <c>autorot</c>.
    /// </summary>
    static Image Load(string path)
    {
        var options = LoaderAcceptsPage(path) ? new VOption { { "page", 0 } } : null;
        using var loaded = Image.NewFromFile(path, kwargs: options);
        return loaded.Autorot();
    }

    /// <summary>
    /// Whether the loader libvips picks for <paramref name="path"/> lists <c>page</c> among its
    /// optional inputs (<c>Vips::Introspect#optional_input</c>).
    /// </summary>
    static bool LoaderAcceptsPage(string path) =>
        Image.FindLoad(path) is { } loader && Introspect.Get(loader).OptionalInput.ContainsKey("page");

    /// <summary>
    /// <c>resize_to_limit(width, height)</c>: <c>thumbnail_image(width, height:, size: :down,
    /// no_rotate: true)</c>, then the default sharpen, <c>conv(SHARPEN_MASK, precision: :integer)</c>.
    /// </summary>
    static Image ResizeToLimit(Image image, int width, int height)
    {
        using var thumbnail = image.ThumbnailImage(width, height: height, size: Enums.Size.Down, noRotate: true);
        using var mask = SharpenMask();
        return thumbnail.Conv(mask, precision: Enums.Precision.Integer);
    }

    /// <summary><c>SHARPEN_MASK</c>: <c>new_from_array([[-1,-1,-1],[-1,32,-1],[-1,-1,-1]], 24)</c>.</summary>
    static Image SharpenMask() => Image.NewFromArray(new double[,] { { -1, -1, -1 }, { -1, 32, -1 }, { -1, -1, -1 } }, 24);
}
