using Campfire.Storage.Blobs;

namespace Campfire.Storage.Variants;

/// <summary>A variant or preview: the blob a representation URL names, and the variation it carries.</summary>
public interface IRepresentation
{
    /// <summary>The original blob: the image for a variant, the video for a preview.</summary>
    Blob Blob { get; }

    Variation Variation { get; }
}

/// <summary>
/// <c>ActiveStorage::VariantWithRecord</c> (Campfire tracks variants): a variation of an image blob,
/// stored once as the <c>image</c> of the <c>active_storage_variant_records</c> row named by
/// <c>(blob_id, variation.digest)</c>.
/// </summary>
public sealed record VariantWithRecord(Blob Blob, Variation Variation) : IRepresentation
{
    /// <summary><c>filename</c>: <c>"#{blob.filename.base}.#{variation.format.downcase}"</c>.</summary>
    public Filename Filename => new($"{Blob.Filename.Base}.{Variation.Format.ToLowerInvariant()}");

    /// <summary><c>content_type</c>, delegated to the variation.</summary>
    public string ContentType => Variation.ContentType;
}

/// <summary>
/// <c>ActiveStorage::Preview</c>: a video's first frame (its <c>preview_image</c> attachment), then that
/// image's variant when the variation has transformations. The URL carries the variation as given,
/// without the image's defaults.
/// </summary>
public sealed record Preview(Blob Blob, Variation Variation) : IRepresentation
{
    /// <summary><c>variant?</c>: <c>variation.transformations.present?</c>.</summary>
    public bool HasVariant => !Variation.Transformations.IsEmpty;

    /// <summary><c>image.variant(variation)</c> for the processed preview image.</summary>
    public VariantWithRecord VariantOf(Blob previewImage) => Representable.Variant(previewImage, Variation);
}

public class RepresentationException(string message) : InvalidOperationException(message);

/// <summary><c>ActiveStorage::InvariableError</c>.</summary>
public sealed class InvariableException(string message) : RepresentationException(message);

/// <summary><c>ActiveStorage::UnpreviewableError</c>.</summary>
public sealed class UnpreviewableException(string message) : RepresentationException(message);

/// <summary><c>ActiveStorage::UnrepresentableError</c>.</summary>
public sealed class UnrepresentableException(string message) : RepresentationException(message);

/// <summary>
/// <c>ActiveStorage::Blob::Representable</c>, and <c>Attachment#variant</c> / <c>#preview</c> /
/// <c>#representation</c> once a named variant is looked up (<see cref="NamedVariants"/>). Building a
/// representation does no image work; processing is <see cref="VariantProcessor"/>'s.
/// </summary>
public static class Representable
{
    /// <summary>
    /// <c>previewable?</c>: some previewer accepts the blob. The reference image installs ffmpeg but
    /// neither poppler nor mupdf, so only <c>VideoPreviewer</c> (<c>blob.video?</c>) ever accepts.
    /// </summary>
    public static bool IsPreviewable(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return blob.IsVideo;
    }

    /// <summary><c>representable?</c></summary>
    public static bool IsRepresentable(Blob blob) => blob.IsVariable || IsPreviewable(blob);

    /// <summary><c>variant(transformations)</c>: the transformations, defaulted to the blob's format.</summary>
    public static VariantWithRecord Variant(Blob blob, Transformations transformations) => Variant(blob, new Variation(transformations));

    /// <summary><c>variant(variation)</c>, as <c>Preview</c> and the representations controller call it.</summary>
    public static VariantWithRecord Variant(Blob blob, Variation variation)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(variation);
        if (!blob.IsVariable)
        {
            throw new InvariableException($"Can't transform blob with ID={blob.Id} and content_type={blob.ContentType}");
        }
        return new VariantWithRecord(blob, variation.DefaultTo(DefaultVariantTransformations(blob)));
    }

    /// <summary><c>preview(transformations)</c>.</summary>
    public static Preview Preview(Blob blob, Transformations transformations) => Preview(blob, new Variation(transformations));

    public static Preview Preview(Blob blob, Variation variation)
    {
        ArgumentNullException.ThrowIfNull(variation);
        if (!IsPreviewable(blob))
        {
            throw new UnpreviewableException($"No previewer found for blob with ID={blob.Id} and content_type={blob.ContentType}");
        }
        return new Preview(blob, variation);
    }

    /// <summary><c>representation(transformations)</c>: a preview when previewable, else a variant.</summary>
    public static IRepresentation Representation(Blob blob, Transformations transformations) => Representation(blob, new Variation(transformations));

    public static IRepresentation Representation(Blob blob, Variation variation)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (IsPreviewable(blob))
        {
            return Preview(blob, variation);
        }
        if (blob.IsVariable)
        {
            return Variant(blob, variation);
        }
        throw new UnrepresentableException(
            $"No previewer found and can't transform blob with ID={blob.Id} and content_type={blob.ContentType}");
    }

    /// <summary><c>default_variant_transformations</c>: <c>{ format: default_variant_format }</c>.</summary>
    public static Transformations DefaultVariantTransformations(Blob blob) => new(("format", DefaultVariantFormat(blob)));

    /// <summary>
    /// <c>default_variant_format</c>: a web image keeps its own format (a String, from the filename or
    /// its content type); anything else becomes <c>:png</c>.
    /// </summary>
    public static object DefaultVariantFormat(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return blob.IsWebImage ? (object?)Format(blob) ?? new RubySymbol("png") : new RubySymbol("png");
    }

    /// <summary>
    /// <c>Blob#format</c>: the filename's extension when Marcel maps it to the blob's content type,
    /// otherwise the content type's first extension.
    /// </summary>
    static string? Format(Blob blob)
    {
        var extension = blob.Filename.Extension;
        if (!string.IsNullOrWhiteSpace(extension) && MarcelExtensions.MimeTypeFor(extension) == blob.ContentType)
        {
            return extension;
        }
        var extensions = MarcelExtensions.ExtensionsOf(blob.ContentType ?? "");
        return extensions.Count > 0 ? extensions[0] : null;
    }
}
