namespace Campfire.Storage.Variants;

/// <summary>
/// The extension lookups Active Storage makes of Marcel for variants: <c>Marcel::Magic.by_extension</c>,
/// <c>Marcel::MimeType.for(extension:)</c> and <c>Marcel::Magic.new(type).extensions</c>.
/// </summary>
static partial class MarcelExtensions
{
    public const string Binary = "application/octet-stream";

    /// <summary>
    /// <c>Marcel::Magic.by_extension(ext)&amp;.type</c>: case-insensitive, with or without the leading dot.
    /// Ruby's <c>downcase</c> is full Unicode; .NET's invariant lowering agrees for every key in the table.
    /// </summary>
    public static string? TypeFor(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var ext = extension.ToLowerInvariant();
        if (ext.StartsWith('.'))
        {
            ext = ext[1..];
        }
        return Types.GetValueOrDefault(ext);
    }

    /// <summary><c>Marcel::MimeType.for(extension:)</c>: the downcased type, or binary.</summary>
    public static string MimeTypeFor(string extension) => TypeFor(extension)?.ToLowerInvariant() ?? Binary;

    /// <summary><c>Marcel::Magic.new(type).extensions</c>.</summary>
    public static IReadOnlyList<string> ExtensionsOf(string contentType) => TypeExtensions.GetValueOrDefault(contentType) ?? [];
}
