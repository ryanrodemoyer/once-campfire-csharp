namespace Campfire.Storage.Variants;

/// <summary>
/// A Ruby Symbol in a transformations hash. Symbols and strings marshal differently, so
/// <c>format: :webp</c> (as the app writes it) and <c>format: "webp"</c> (as a decoded variation key
/// carries it) have different digests and so different variant records.
/// </summary>
public readonly record struct RubySymbol(string Name)
{
    public override string ToString() => Name;
}
