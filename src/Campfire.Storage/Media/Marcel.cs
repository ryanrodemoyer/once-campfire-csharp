using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;

namespace Campfire.Storage.Media;

/// <summary>
/// Marcel 1.1.0's <c>Marcel::MimeType.for(io, name:, declared_type:)</c>
/// (marcel/lib/marcel/mime_type.rb, magic.rb), as <c>ActiveStorage::Blob#extract_content_type</c>
/// calls it on upload. The magic table and type parents are generated from the gem
/// (<c>marcel_magic.rb</c>); the extension table is S02's <see cref="MarcelExtensions"/>.
/// </summary>
public static partial class Marcel
{
    public const string Binary = "application/octet-stream";

    /// <summary>One <c>[offset, value, children]</c> entry; <paramref name="RangeEnd"/> is set when the offset is a Range.</summary>
    sealed record MagicMatch(int Offset, int? RangeEnd, byte[]? Value, MagicMatch[] Children);

    /// <summary>
    /// How many leading bytes the magic table can look at: identifying this prefix of a file gives
    /// the same answer as identifying all of it. Lazy, since the table is initialized in the
    /// generated part of this class.
    /// </summary>
    static readonly Lazy<int> MagicReach = new(() => Magic.Max(entry => Reach(entry.Matches)));

    /// <summary><c>split(/[;,\s]/, 2)</c>'s separators.</summary>
    static readonly System.Buffers.SearchValues<char> MediaTypeEnd = System.Buffers.SearchValues.Create(";, \t\n\v\f\r");

    /// <summary><see cref="ContentTypeIdentifier"/> for <see cref="BlobStorage"/>: Marcel over the staged file.</summary>
    public static readonly ContentTypeIdentifier Identify = For;

    /// <summary>
    /// <c>Marcel::MimeType.for(io, name:, declared_type:)</c>: the magic type, unless the declared
    /// or name type is more specific (a child of it); binary when nothing matches.
    /// <paramref name="content"/> is read from its current position.
    /// </summary>
    public static string For(Stream content, string? name, string? declaredType)
    {
        ArgumentNullException.ThrowIfNull(content);
        var prefix = new byte[MagicReach.Value];
        var length = content.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        return For(prefix.AsSpan(0, length), name, declaredType);
    }

    /// <inheritdoc cref="For(Stream, string?, string?)"/>
    public static string For(ReadOnlySpan<byte> data, string? name, string? declaredType)
    {
        var filenameType = name is null ? null : ByPath(name);
        return MostSpecificType(ByMagic(data), ForDeclaredType(declaredType), filenameType);
    }

    /// <summary><c>Marcel::Magic.by_magic(io)&amp;.type&amp;.downcase</c>: the first table entry whose matches hit.</summary>
    public static string? ByMagic(ReadOnlySpan<byte> data)
    {
        foreach (var (type, matches) in Magic)
        {
            if (MatchesAny(data, matches))
            {
                return type.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary><c>Marcel::Magic.by_path(name)&amp;.type&amp;.downcase</c>: by <c>File.extname</c>.</summary>
    public static string? ByPath(string name) => MarcelExtensions.TypeFor(new Filename(name).ExtensionWithDelimiter)?.ToLowerInvariant();

    /// <summary><c>Marcel::Magic.child?(child, parent)</c>.</summary>
    public static bool IsChild(string child, string parent) =>
        child == parent || (Parents.TryGetValue(child, out var parents) && parents.Any(p => IsChild(p, parent)));

    /// <summary>
    /// <c>magic_match_io</c>. A fixed offset reads <c>value.bytesize</c> bytes there and compares; a
    /// Range offset reads from its start to its end plus the value's size and looks for the value
    /// anywhere in it. A read that starts at or past the end is Ruby's <c>nil</c>, which never matches.
    /// </summary>
    static bool MatchesAny(ReadOnlySpan<byte> data, MagicMatch[] matches)
    {
        foreach (var match in matches)
        {
            if (match.Value is not { } value)
            {
                continue;
            }
            var hit = match.RangeEnd is { } end
                ? Read(data, match.Offset, end - match.Offset + value.Length, out var window) && window.IndexOf(value) >= 0
                : Read(data, match.Offset, value.Length, out var bytes) && bytes.SequenceEqual(value);
            if (hit && (match.Children.Length == 0 || MatchesAny(data, match.Children)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary><c>io.read(offset); io.read(length)</c>: false where Ruby reads <c>nil</c>, else up to <paramref name="length"/> bytes.</summary>
    static bool Read(ReadOnlySpan<byte> data, int offset, int length, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (length == 0)
        {
            return true;
        }
        if (offset >= data.Length)
        {
            return false;
        }
        bytes = data[offset..Math.Min(data.Length, offset + length)];
        return true;
    }

    static int Reach(MagicMatch[] matches) => matches.Length == 0
        ? 0
        : matches.Max(m => Math.Max((m.RangeEnd ?? m.Offset) + (m.Value?.Length ?? 0), Reach(m.Children)));

    /// <summary>
    /// <c>for_declared_type</c>: the downcased media type before any <c>;</c>, <c>,</c> or whitespace,
    /// if it has a <c>/</c>; a declared binary type counts as undeclared.
    /// </summary>
    static string? ForDeclaredType(string? declaredType)
    {
        if (declaredType is null)
        {
            return null;
        }
        var lowered = declaredType.ToLowerInvariant();
        var end = lowered.AsSpan().IndexOfAny(MediaTypeEnd);
        var type = end < 0 ? lowered : lowered[..end];
        return type.Contains('/', StringComparison.Ordinal) && type != Binary ? type : null;
    }

    /// <summary>
    /// <c>most_specific_type(*candidates, BINARY)</c>: <c>compact.uniq.reduce</c>, where a later
    /// candidate replaces the pick only when it is a child of it.
    /// </summary>
    static string MostSpecificType(params ReadOnlySpan<string?> candidates)
    {
        string? pick = null;
        var seen = new List<string>();
        foreach (var candidate in (string?[])[.. candidates, Binary])
        {
            if (candidate is null || seen.Contains(candidate))
            {
                continue;
            }
            seen.Add(candidate);
            pick = pick is null || IsChild(candidate, pick) ? candidate : pick;
        }
        return pick!;
    }
}
