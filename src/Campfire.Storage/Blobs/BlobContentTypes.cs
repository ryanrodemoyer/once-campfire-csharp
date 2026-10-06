using System.Collections.Frozen;

namespace Campfire.Storage.Blobs;

/// <summary>
/// The content type lists Active Storage's engine configures (activestorage/lib/active_storage/
/// engine.rb), as Campfire leaves them, except that reference/config/initializers/vips.rb drops
/// BMP, ICO and PSD from the variable types.
/// </summary>
public static class BlobContentTypes
{
    /// <summary><c>ActiveStorage.binary_content_type</c>.</summary>
    public const string Binary = "application/octet-stream";

    public static readonly FrozenSet<string> Variable = FrozenSet.Create(StringComparer.Ordinal,
        "image/png", "image/gif", "image/jpeg", "image/tiff", "image/webp", "image/avif", "image/heic", "image/heif");

    public static readonly FrozenSet<string> WebImage = FrozenSet.Create(StringComparer.Ordinal,
        "image/png", "image/jpeg", "image/gif");

    public static readonly FrozenSet<string> ServeAsBinary = FrozenSet.Create(StringComparer.Ordinal,
        "text/html", "image/svg+xml", "application/postscript", "application/x-shockwave-flash", "text/xml",
        "application/xml", "application/xhtml+xml", "application/mathml+xml", "text/cache-manifest");

    public static readonly FrozenSet<string> AllowedInline = FrozenSet.Create(StringComparer.Ordinal,
        "image/webp", "image/avif", "image/png", "image/gif", "image/jpeg", "image/tiff", "image/bmp",
        "image/vnd.adobe.photoshop", "image/vnd.microsoft.icon", "application/pdf");
}
