using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using Campfire.RailsCompat.Ruby;

namespace Campfire.RailsCompat.Cookies;

/// <summary>
/// Rack and Rails wire escaping and Set-Cookie header formatting.
/// </summary>
public static class CookieEncoding
{
    public const int MaxCookieSize = 4096;
    public static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Escapes a raw cookie value using Rack's escaping rule (<c>URI.encode_www_form_component</c>).
    /// </summary>
    public static string Escape(string raw) => RubyEscape.RackEscape(raw);

    /// <summary>
    /// Unescapes a wire cookie value: '+' becomes space, %XX is hex-decoded.
    /// Malformed % sequences or invalid UTF-8 leave the value untouched (<c>Rack::Utils.unescape rescue value</c>).
    /// </summary>
    public static string Unescape(string wire) => Unescape(wire.AsSpan());

    const int stackBytes = 512;

    static string Unescape(ReadOnlySpan<char> wire)
    {
        // Plain ASCII with nothing to decode is its own unescaped value.
        if (wire.IndexOfAny('%', '+') < 0 && Ascii.IsValid(wire))
        {
            return wire.ToString();
        }

        var maxBytes = Encoding.UTF8.GetMaxByteCount(wire.Length);
        byte[]? rented = null;
        Span<byte> bytes = maxBytes <= stackBytes ? stackalloc byte[stackBytes] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var length = Encoding.UTF8.GetBytes(wire, bytes);
            // Decoded in place: a byte is written at or before the one it was read from.
            var outLen = 0;
            var i = 0;
            while (i < length)
            {
                var b = bytes[i];
                if (b == (byte)'+')
                {
                    bytes[outLen++] = (byte)' ';
                    i++;
                }
                else if (b == (byte)'%')
                {
                    if (i + 2 < length &&
                        char.IsAsciiHexDigit((char)bytes[i + 1]) &&
                        char.IsAsciiHexDigit((char)bytes[i + 2]))
                    {
                        bytes[outLen++] = (byte)((HexVal(bytes[i + 1]) << 4) | HexVal(bytes[i + 2]));
                        i += 3;
                    }
                    else
                    {
                        return wire.ToString();
                    }
                }
                else
                {
                    bytes[outLen++] = b;
                    i++;
                }
            }

            var decoded = bytes[..outLen];
            return Utf8.IsValid(decoded) ? Encoding.UTF8.GetString(decoded) : wire.ToString();
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    static int HexVal(byte digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

    /// <summary>
    /// Parses a single Cookie header string into key-value pairs (unescaped), keeping first occurrence.
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseCookieHeader(string? header)
    {
        var result = new List<KeyValuePair<string, string>>();
        HashSet<string>? seen = null;
        if (!string.IsNullOrEmpty(header))
        {
            AddPairs(header, result, ref seen);
        }

        return result;
    }

    /// <summary>
    /// Parses multiple Cookie headers into key-value pairs, keeping first occurrence.
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseCookieHeaders(IEnumerable<string>? headers)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (headers is null) return result;

        HashSet<string>? seen = null;
        foreach (var header in headers)
        {
            AddPairs(header, result, ref seen);
        }

        return result;
    }

    // A few cookies are checked against each other by looking; a set takes over for the odd request with many.
    const int scanLimit = 16;

    /// <summary><c>header.split(";")</c>, each part but the first with its leading spaces dropped, as pairs.</summary>
    static void AddPairs(ReadOnlySpan<char> header, List<KeyValuePair<string, string>> result, ref HashSet<string>? seen)
    {
        var first = true;
        while (true)
        {
            var semicolon = header.IndexOf(';');
            var part = semicolon < 0 ? header : header[..semicolon];
            if (!first)
            {
                part = part.TrimStart(' ');
            }
            first = false;

            if (part.Length > 0)
            {
                var eq = part.IndexOf('=');
                var key = eq < 0 ? part : part[..eq];
                var value = eq < 0 ? default : part[(eq + 1)..];
                if (Unseen(key, result, ref seen))
                {
                    var pair = new KeyValuePair<string, string>(key.ToString(), Unescape(value));
                    result.Add(pair);
                    seen?.Add(pair.Key);
                }
            }

            if (semicolon < 0)
            {
                return;
            }
            header = header[(semicolon + 1)..];
        }
    }

    static bool Unseen(ReadOnlySpan<char> key, List<KeyValuePair<string, string>> result, ref HashSet<string>? seen)
    {
        if (seen is null && result.Count >= scanLimit)
        {
            seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in result)
            {
                seen.Add(pair.Key);
            }
        }
        if (seen is not null)
        {
            return !seen.GetAlternateLookup<ReadOnlySpan<char>>().Contains(key);
        }
        foreach (var pair in result)
        {
            if (key.SequenceEqual(pair.Key))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Formats a Set-Cookie header matching Rack / Rails.
    /// </summary>
    public static string FormatSetCookie(string name, string rawValue, CookieOptions options, DateTimeOffset? now = null)
    {
        // A session cookie is about a kilobyte: the stack holds it, and one string comes out.
        var builder = new DefaultInterpolatedStringHandler(0, 0, null, stackalloc char[1536]);
        builder.AppendLiteral(name);
        builder.AppendLiteral("=");
        RubyEscape.AppendRackEscaped(ref builder, rawValue);

        if (!string.IsNullOrEmpty(options.Domain))
        {
            builder.AppendLiteral("; domain=");
            builder.AppendLiteral(options.Domain);
        }

        if (!string.IsNullOrEmpty(options.Path))
        {
            builder.AppendLiteral("; path=");
            builder.AppendLiteral(options.Path);
        }

        var expires = options.Expires;
        if (options.Permanent && expires is null && now.HasValue)
        {
            expires = now.Value.AddYears(20);
        }

        if (expires.HasValue)
        {
            Span<char> date = stackalloc char[40];
            expires.Value.ToUniversalTime().TryFormat(date, out var written, "r", CultureInfo.InvariantCulture);
            builder.AppendLiteral("; expires=");
            builder.AppendFormatted(date[..written]);
        }

        if (options.Secure)
        {
            builder.AppendLiteral("; secure");
        }

        if (options.HttpOnly)
        {
            builder.AppendLiteral("; httponly");
        }

        if (options.SameSite.HasValue)
        {
            builder.AppendLiteral("; samesite=");
            builder.AppendLiteral(options.SameSite.Value switch
            {
                SameSiteMode.Lax => "lax",
                SameSiteMode.Strict => "strict",
                SameSiteMode.None => "none",
                _ => "lax"
            });
        }

        if (options.Partitioned)
        {
            builder.AppendLiteral("; partitioned");
        }

        return builder.ToStringAndClear();
    }

    /// <summary>
    /// Formats a delete Set-Cookie header matching Rack / Rails.
    /// </summary>
    public static string FormatDeleteSetCookie(string name, CookieOptions? options = null)
    {
        var path = options?.Path ?? "/";
        var builder = new StringBuilder();
        builder.Append(name).Append('=');

        if (!string.IsNullOrEmpty(options?.Domain))
        {
            builder.Append("; domain=").Append(options.Domain);
        }

        builder.Append("; path=").Append(path);
        builder.Append("; max-age=0; expires=").Append(Epoch.ToString("r", CultureInfo.InvariantCulture));

        if (options?.SameSite.HasValue == true)
        {
            builder.Append("; samesite=").Append(options.SameSite.Value switch
            {
                SameSiteMode.Lax => "lax",
                SameSiteMode.Strict => "strict",
                SameSiteMode.None => "none",
                _ => "lax"
            });
        }
        else if (options is null)
        {
            builder.Append("; samesite=lax");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Checks that the total byte size of cookie name and value does not exceed <see cref="MaxCookieSize"/>.
    /// </summary>
    public static void CheckOverflow(string name, string value)
    {
        var total = Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
        if (total > MaxCookieSize)
        {
            throw new CookieOverflowException($"{name} cookie overflowed with size {total} bytes");
        }
    }
}
