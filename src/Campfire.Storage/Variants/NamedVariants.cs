namespace Campfire.Storage.Variants;

/// <summary>
/// The transformations the app asks for, in the order its Ruby hashes list them. Representations
/// default them (<see cref="Representable.Variant(Blobs.Blob, Transformations)"/>) before digesting.
/// </summary>
public static class NamedVariants
{
    /// <summary><c>Message::THUMBNAIL_MAX_WIDTH</c> (reference/app/models/message/attachment.rb).</summary>
    public const long ThumbnailMaxWidth = 1200;

    /// <summary><c>Message::THUMBNAIL_MAX_HEIGHT</c>.</summary>
    public const long ThumbnailMaxHeight = 800;

    /// <summary>Message <c>attachment</c>'s <c>:thumb</c>: <c>resize_to_limit: [1200, 800]</c>.</summary>
    public static Transformations MessageThumb { get; } = new(("resize_to_limit", new[] { ThumbnailMaxWidth, ThumbnailMaxHeight }));

    /// <summary>
    /// <c>attachment.preview(format: :webp)</c>, which <c>Message#process_attachment</c> processes for a
    /// video.
    /// </summary>
    public static Transformations VideoPreview { get; } = new(("format", new RubySymbol("webp")));

    /// <summary>
    /// The video poster, <c>attachment.preview(format: :webp, resize_to_limit: [1200, 800])</c>
    /// (reference/app/helpers/messages/attachment_presentation.rb).
    /// </summary>
    public static Transformations VideoPoster { get; } = new(
        ("format", new RubySymbol("webp")), ("resize_to_limit", new[] { ThumbnailMaxWidth, ThumbnailMaxHeight }));

    /// <summary>User <c>avatar</c>'s <c>:square</c> (reference/app/models/user/avatar.rb).</summary>
    public static Transformations AvatarSquare { get; } = new(("resize_to_limit", new[] { 512L, 512L }), ("format", new RubySymbol("webp")));

    /// <summary>Account <c>logo</c>'s <c>:large</c> (reference/app/models/account.rb).</summary>
    public static Transformations LogoLarge { get; } = new(("resize_to_limit", new[] { 512L, 512L }), ("format", new RubySymbol("png")));

    /// <summary>Account <c>logo</c>'s <c>:small</c>.</summary>
    public static Transformations LogoSmall { get; } = new(("resize_to_limit", new[] { 192L, 192L }), ("format", new RubySymbol("png")));
}
