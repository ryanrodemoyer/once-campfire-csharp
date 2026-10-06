using System.Globalization;
using System.Text;
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
    public static string Unescape(string wire)
    {
        var bytes = Encoding.UTF8.GetBytes(wire);
        var outBytes = new byte[bytes.Length];
        var outLen = 0;
        var i = 0;

        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (b == (byte)'+')
            {
                outBytes[outLen++] = (byte)' ';
                i++;
            }
            else if (b == (byte)'%')
            {
                if (i + 2 < bytes.Length &&
                    char.IsAsciiHexDigit((char)bytes[i + 1]) &&
                    char.IsAsciiHexDigit((char)bytes[i + 2]))
                {
                    var h1 = HexVal(bytes[i + 1]);
                    var h2 = HexVal(bytes[i + 2]);
                    outBytes[outLen++] = (byte)((h1 << 4) | h2);
                    i += 3;
                }
                else
                {
                    return wire;
                }
            }
            else
            {
                outBytes[outLen++] = b;
                i++;
            }
        }

        try
        {
            return StrictUtf8.GetString(outBytes, 0, outLen);
        }
        catch
        {
            return wire;
        }
    }

    static int HexVal(byte digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

    /// <summary>
    /// Parses a single Cookie header string into key-value pairs (unescaped), keeping first occurrence.
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseCookieHeader(string? header)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(header))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = header.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = i == 0 ? parts[i] : parts[i].TrimStart(' ');
            if (part.Length == 0) continue;

            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];

            if (seen.Add(key))
            {
                result.Add(new KeyValuePair<string, string>(key, Unescape(value)));
            }
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            var parts = header.Split(';');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = i == 0 ? parts[i] : parts[i].TrimStart(' ');
                if (part.Length == 0) continue;

                var eq = part.IndexOf('=');
                var key = eq < 0 ? part : part[..eq];
                var value = eq < 0 ? "" : part[(eq + 1)..];

                if (seen.Add(key))
                {
                    result.Add(new KeyValuePair<string, string>(key, Unescape(value)));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Formats a Set-Cookie header matching Rack / Rails.
    /// </summary>
    public static string FormatSetCookie(string name, string rawValue, CookieOptions options, DateTimeOffset? now = null)
    {
        var builder = new StringBuilder();
        builder.Append(name).Append('=').Append(Escape(rawValue));

        if (!string.IsNullOrEmpty(options.Domain))
        {
            builder.Append("; domain=").Append(options.Domain);
        }

        if (!string.IsNullOrEmpty(options.Path))
        {
            builder.Append("; path=").Append(options.Path);
        }

        var expires = options.Expires;
        if (options.Permanent && expires is null && now.HasValue)
        {
            expires = now.Value.AddYears(20);
        }

        if (expires.HasValue)
        {
            builder.Append("; expires=").Append(expires.Value.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture));
        }

        if (options.Secure)
        {
            builder.Append("; secure");
        }

        if (options.HttpOnly)
        {
            builder.Append("; httponly");
        }

        if (options.SameSite.HasValue)
        {
            builder.Append("; samesite=").Append(options.SameSite.Value switch
            {
                SameSiteMode.Lax => "lax",
                SameSiteMode.Strict => "strict",
                SameSiteMode.None => "none",
                _ => "lax"
            });
        }

        if (options.Partitioned)
        {
            builder.Append("; partitioned");
        }

        return builder.ToString();
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
