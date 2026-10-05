namespace Campfire.Templates;

/// <summary>
/// A string already marked HTML-safe, the counterpart of <c>ActiveSupport::SafeBuffer</c>
/// (<c>"...".html_safe</c>). Templates append it without escaping; a plain <see cref="string"/> is
/// always escaped. <c>default(SafeString)</c> is empty.
/// </summary>
public readonly struct SafeString(string? value) : IEquatable<SafeString>
{
    readonly string? value = value;

    public static SafeString Empty => default;

    public string Value => value ?? "";

    /// <summary>Escapes <paramref name="text"/> like <c>ERB::Util.html_escape</c>.</summary>
    public static SafeString Escape(string? text) => new(HtmlEscaper.Escape(text ?? ""));

    public override string ToString() => Value;

    public bool Equals(SafeString other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SafeString other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(SafeString left, SafeString right) => left.Equals(right);

    public static bool operator !=(SafeString left, SafeString right) => !left.Equals(right);
}
