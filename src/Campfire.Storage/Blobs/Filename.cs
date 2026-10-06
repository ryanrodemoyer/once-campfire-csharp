using Campfire.RailsCompat.Ruby;

namespace Campfire.Storage.Blobs;

/// <summary>
/// <c>ActiveStorage::Filename</c> (activestorage/app/models/active_storage/filename.rb): the stored
/// <c>active_storage_blobs.filename</c>, with Ruby's <c>File.extname</c> and <c>File.basename</c>
/// for its parts. Everything shown or put in a URL or header is <see cref="Sanitized"/>.
/// </summary>
public sealed record Filename(string Value)
{
    const string unsafeCharacters = "‮%$|:;/<>?*\"\t\r\n\\";

    /// <summary><c>File.basename(filename, extension_with_delimiter)</c>.</summary>
    public string Base
    {
        get
        {
            var name = Basename(Value);
            var extension = ExtensionWithDelimiter;
            return extension.Length > 0 && name.Length > extension.Length && name.EndsWith(extension, StringComparison.Ordinal)
                ? name[..^extension.Length]
                : name;
        }
    }

    /// <summary><c>File.extname(filename)</c>: <c>".jpg"</c>, or <c>""</c>.</summary>
    public string ExtensionWithDelimiter => Extname(Value);

    /// <summary><c>extension</c> / <c>extension_without_delimiter</c>.</summary>
    public string Extension => ExtensionWithDelimiter.Length > 0 ? ExtensionWithDelimiter[1..] : "";

    /// <summary>
    /// <c>sanitized</c>: <c>strip</c>, then the RTL override, path separators and shell and HTML
    /// metacharacters become <c>-</c>. Invalid UTF-8 was already replaced with U+FFFD when the
    /// bytes became a .NET string (see <see cref="FromBytes"/>), as <c>encode(invalid: :replace)</c> does.
    /// </summary>
    public string Sanitized => string.Create(RubyString.Strip(Value).Length, RubyString.Strip(Value), static (span, stripped) =>
    {
        for (var i = 0; i < stripped.Length; i++)
        {
            span[i] = unsafeCharacters.Contains(stripped[i], StringComparison.Ordinal) ? '-' : stripped[i];
        }
    });

    /// <summary>A filename from raw bytes, each invalid UTF-8 sequence replaced with U+FFFD.</summary>
    public static Filename FromBytes(ReadOnlySpan<byte> bytes) => new(System.Text.Encoding.UTF8.GetString(bytes));

    public override string ToString() => Sanitized;

    /// <summary><c>File.basename(path)</c>: the last component, ignoring trailing slashes.</summary>
    static string Basename(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return path.Length == 0 ? "" : "/";
        }
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// <c>File.extname(path)</c> on Unix: leading dots don't start an extension, and a trailing dot
    /// is an extension of its own (<c>"foo."</c> → <c>"."</c>).
    /// </summary>
    static string Extname(string path)
    {
        var name = Basename(path).TrimStart('.');
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }
}
