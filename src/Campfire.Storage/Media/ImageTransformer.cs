using Campfire.Storage.Variants;
using NetVips;

namespace Campfire.Storage.Media;

/// <summary>
/// <c>ActiveStorage::Transformers::Vips</c> (activestorage/lib/active_storage/transformers/
/// image_processing_transformer.rb) over image_processing 1.14's <c>ImageProcessing::Vips</c>:
/// <c>source(file).loader(page: 0).convert(format).apply(operations).call</c>. Because a loader
/// option is set, the image is loaded first (not resized on load), auto-rotated, then each
/// operation runs, and the result is written by the saver libvips picks for the format's
/// extension with its defaults: no metadata stripping, the saver's own quality.
/// </summary>
public static class ImageTransformer
{
    /// <summary><c>Vips::MAX_COORD</c>, which <c>default_dimensions</c> puts in for a missing dimension.</summary>
    public const int MaxCoord = 10_000_000;

    /// <summary>
    /// <c>variation.transform(file)</c>: transforms the image at <paramref name="input"/> and writes it
    /// to <paramref name="output"/>, whose extension (<c>.&lt;format&gt;</c>) picks the saver, as
    /// image_processing's tempfile does.
    /// </summary>
    public static void Transform(string input, Variation variation, string output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(variation);
        ArgumentNullException.ThrowIfNull(output);
        var operations = Operations(variation.Transformations);
        LibVips.EnsureInitialized();
        try
        {
            using var image = Load(input);
            var current = image;
            try
            {
                foreach (var (width, height) in operations)
                {
                    var resized = ResizeToLimit(current, width, height);
                    if (current != image)
                    {
                        current.Dispose();
                    }
                    current = resized;
                }
                current.WriteToFile(output);
            }
            finally
            {
                if (current != image)
                {
                    current.Dispose();
                }
            }
        }
        catch (VipsException exception)
        {
            throw new MediaException(exception.Message, exception);
        }
    }

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

    /// <summary>
    /// <c>ImageProcessingTransformer#operations</c>: every transformation but <c>format</c>, skipping
    /// blank arguments, and refusing <c>combine_options</c>. Campfire defines only
    /// <c>resize_to_limit</c>, so other operations aren't supported.
    /// </summary>
    public static List<(int Width, int Height)> Operations(Transformations transformations)
    {
        var operations = new List<(int, int)>();
        foreach (var (name, argument) in transformations.Entries)
        {
            if (name == "combine_options")
            {
                throw new ArgumentException(
                    "Active Storage's ImageProcessing transformer doesn't support :combine_options, as it always generates a single command.");
            }
            if (name == "format" || IsBlank(argument))
            {
                continue;
            }
            if (name != "resize_to_limit" || argument is not object?[] { Length: 2 } dimensions)
            {
                throw new NotSupportedException($"Unsupported transformation {name}: {argument}");
            }
            if (dimensions[0] is null && dimensions[1] is null)
            {
                throw new ArgumentException("either width or height must be specified");
            }
            operations.Add((Dimension(dimensions[0]), Dimension(dimensions[1])));
        }
        return operations;
    }

    static int Dimension(object? value) => value switch
    {
        null => MaxCoord,
        long n when n is >= int.MinValue and <= int.MaxValue => (int)n,
        _ => throw new NotSupportedException($"Unsupported resize_to_limit dimension {value}"),
    };

    /// <summary><c>Object#blank?</c> for the values a transformation holds.</summary>
    static bool IsBlank(object? value) => value switch
    {
        null or false => true,
        string s => s.All(char.IsWhiteSpace),
        RubySymbol symbol => symbol.Name.Length == 0,
        object?[] items => items.Length == 0,
        Transformations hash => hash.IsEmpty,
        _ => false,
    };
}
