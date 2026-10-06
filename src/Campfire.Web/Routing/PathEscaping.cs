using System.Buffers;
using System.Text;

namespace Campfire.Web.Routing;

/// <summary>
/// <c>ActionDispatch::Journey::Router::Utils</c> (actionpack <c>journey/router/utils.rb</c>): path
/// normalization before recognition, and RFC 3986 escaping for generated paths.
/// </summary>
public static class PathEscaping
{
    const string unreserved = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
    const string subDelims = "!$&'()*+,;=";

    static readonly SearchValues<char> SegmentSafe = SearchValues.Create(unreserved + subDelims + ":@");
    static readonly SearchValues<char> PathSafe = SearchValues.Create(unreserved + subDelims + ":@/");
    static readonly SearchValues<char> FragmentSafe = SearchValues.Create(unreserved + subDelims + ":@/?");

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// <c>normalize_path</c>: one leading slash, repeated slashes squeezed, the trailing slash
    /// dropped and lowercase percent-escapes upcased (only those spelled with <c>[a-f0-9]</c>).
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (path is null)
        {
            return "/";
        }
        if (path == "/" || (path.StartsWith('/') && !path.EndsWith('/') && !path.Contains('%') && !path.Contains("//", StringComparison.Ordinal)))
        {
            return path;
        }

        var normalized = new StringBuilder(path.Length + 1);
        foreach (var c in "/" + path)
        {
            if (c != '/' || normalized.Length == 0 || normalized[^1] != '/')
            {
                normalized.Append(c);
            }
        }
        if (normalized.Length == 1)
        {
            return "/";
        }
        if (normalized[^1] == '/')
        {
            normalized.Length--;
        }
        for (var i = 0; i + 2 < normalized.Length; i++)
        {
            if (normalized[i] == '%' && IsLowerHex(normalized[i + 1]) && IsLowerHex(normalized[i + 2]))
            {
                normalized[i + 1] = char.ToUpperInvariant(normalized[i + 1]);
                normalized[i + 2] = char.ToUpperInvariant(normalized[i + 2]);
                i += 2;
            }
        }
        return normalized.ToString();
    }

    /// <summary>
    /// A captured path parameter as <c>Journey::Router#find_routes</c> stores it: percent-decoded
    /// with <c>CGI.unescapeURIComponent</c> when it contains <c>%</c> ('+' stays '+'), then checked
    /// to be valid UTF-8 (<c>ActionController::BadRequest</c> otherwise).
    /// </summary>
    public static string UnescapePathParameter(string name, string value)
    {
        if (!value.Contains('%'))
        {
            return value;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        var decoded = new byte[bytes.Length];
        var length = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && char.IsAsciiHexDigit((char)bytes[i + 1]) && char.IsAsciiHexDigit((char)bytes[i + 2]))
            {
                decoded[length++] = (byte)Convert.ToInt32(Encoding.ASCII.GetString(bytes, i + 1, 2), 16);
                i += 2;
            }
            else
            {
                decoded[length++] = bytes[i];
            }
        }
        try
        {
            return StrictUtf8.GetString(decoded, 0, length);
        }
        catch (DecoderFallbackException)
        {
            throw new BadRequestException($"Invalid path parameters: Invalid encoding for parameter: {name}");
        }
    }

    /// <summary><c>Utils.escape_segment</c>: what a <c>:param</c> generates.</summary>
    public static string EscapeSegment(string segment) => Escape(segment, SegmentSafe);

    /// <summary><c>Utils.escape_path</c>: what a <c>*glob</c> generates (slashes stay).</summary>
    public static string EscapePath(string path) => Escape(path, PathSafe);

    /// <summary><c>Utils.escape_fragment</c>: what the <c>anchor:</c> option generates.</summary>
    public static string EscapeFragment(string fragment) => Escape(fragment, FragmentSafe);

    static string Escape(string value, SearchValues<char> safe)
    {
        if (!value.AsSpan().ContainsAnyExcept(safe))
        {
            return value;
        }
        var escaped = new StringBuilder(value.Length * 3);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (safe.Contains(c))
            {
                escaped.Append(c);
                continue;
            }
            var width = char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            var count = Encoding.UTF8.GetBytes(value.AsSpan(i, width), utf8);
            for (var b = 0; b < count; b++)
            {
                escaped.Append('%').Append(utf8[b].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            i += width - 1;
        }
        return escaped.ToString();
    }

    static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
}
