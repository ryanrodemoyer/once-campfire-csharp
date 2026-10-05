using System.Text;

namespace Campfire.RichText.Sanitize;

public class RubyUriException(string message) : Exception(message);

public class RubyUriInvalidComponentException(string message) : RubyUriException(message);

/// <summary>
/// A port of Ruby's RFC 3986 URI.parse (uri 1.1) as relied on by ActionText and Campfire.
/// </summary>
public sealed class RubyUri
{
    public string? Scheme { get; set; }
    public string? Userinfo { get; set; }
    public string? Host { get; set; }
    public ulong? Port { get; set; }
    public string? Path { get; set; }
    public string? Opaque { get; set; }
    public string? Query { get; set; }
    public string? Fragment { get; set; }

    public bool IsHttp =>
        Scheme is not null && (Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

    public ulong? DefaultPort => Scheme?.ToUpperInvariant() switch
    {
        "HTTP" or "WS" => 80,
        "HTTPS" or "WSS" => 443,
        "FTP" => 21,
        "LDAP" => 389,
        "LDAPS" => 636,
        _ => null
    };

    public string ToUriString()
    {
        var sb = new StringBuilder();
        if (Scheme is not null)
        {
            sb.Append(Scheme).Append(':');
        }

        if (Opaque is not null)
        {
            sb.Append(Opaque);
        }
        else
        {
            if (Host is not null || Scheme == "file" || Scheme == "postgres")
            {
                sb.Append("//");
            }

            if (Userinfo is not null)
            {
                sb.Append(Userinfo).Append('@');
            }

            if (Host is not null)
            {
                sb.Append(Host);
            }

            if (Port is not null && Port != DefaultPort)
            {
                sb.Append(':').Append(Port);
            }

            sb.Append(Path ?? string.Empty);

            if (Query is not null)
            {
                sb.Append('?').Append(Query);
            }
        }

        if (Fragment is not null)
        {
            sb.Append('#').Append(Fragment);
        }

        return sb.ToString();
    }

    public static RubyUri Parse(string value)
    {
        if (value.Any(c => c > 127))
        {
            throw new RubyUriException("bad URI (is not URI?): non-ASCII characters");
        }

        var uri = SplitAbsolute(value) ?? SplitRelative(value) ??
                  throw new RubyUriException("bad URI (is not URI?)");

        if (uri.Query is not null)
        {
            uri.Query = EscapeQuery(uri.Query);
        }

        uri.Port ??= uri.DefaultPort;
        CheckSchemeClass(uri);
        return uri;
    }

    static void CheckSchemeClass(RubyUri uri)
    {
        switch (uri.Scheme?.ToUpperInvariant())
        {
            case "MAILTO":
                var opaque = uri.Opaque ?? (uri.Query is not null ? $"?{uri.Query}" : null);
                if (opaque is null)
                {
                    throw new RubyUriInvalidComponentException("unrecognised opaque part for mailtoURL");
                }
                var to = opaque.Split('?')[0];
                if (!MailtoToValid(to))
                {
                    throw new RubyUriInvalidComponentException("unrecognised opaque part for mailtoURL");
                }
                break;
            case "LDAP" or "LDAPS":
                if (uri.Fragment is not null || uri.Path is null)
                {
                    throw new RubyUriException("bad LDAP URI");
                }
                break;
            case "FTP":
                if (uri.Path is null)
                {
                    throw new RubyUriException("bad FTP URI");
                }
                break;
        }
    }

    static bool MailtoToValid(string to)
    {
        var bytes = Encoding.ASCII.GetBytes(to);
        var i = 0;
        static bool Special(byte b) => b is (byte)'@' or (byte)',' or (byte)';';

        while (i < bytes.Length)
        {
            var start = i;
            while (i < bytes.Length && !Special(bytes[i]))
            {
                i++;
            }

            if (i == start || i >= bytes.Length || bytes[i] != (byte)'@')
            {
                return false;
            }

            i++; // skip @
            var domain = i;
            while (i < bytes.Length && !Special(bytes[i]))
            {
                i++;
            }

            if (i == domain)
            {
                return false;
            }

            if (i < bytes.Length)
            {
                if (bytes[i] == (byte)'@')
                {
                    return false;
                }
                i++;
            }
        }

        return true;
    }

    static string EscapeQuery(string query)
    {
        var cleaned = query.Where(c => c is not '\t' and not '\r' and not '\n').Select(c => (byte)c).ToArray();
        for (var w = 0; w + 2 < cleaned.Length; w++)
        {
            if (cleaned[w] == (byte)'%' && (!char.IsAsciiHexDigit((char)cleaned[w + 1]) || !char.IsAsciiHexDigit((char)cleaned[w + 2])))
            {
                throw new RubyUriException("bad URI: invalid percent escape in query");
            }
        }

        var sb = new StringBuilder();
        var i = 0;
        while (i < cleaned.Length)
        {
            var b = cleaned[i];
            var isEscape = i + 2 < cleaned.Length && b == (byte)'%' && char.IsAsciiHexDigit((char)cleaned[i + 1]) && char.IsAsciiHexDigit((char)cleaned[i + 2]);
            if (isEscape || b == (byte)'!' || (b >= (byte)'$' && b <= (byte)'&') || (b >= (byte)'(' && b <= (byte)';') || b == (byte)'=' || (b >= (byte)'?' && b <= (byte)'_') || (b >= (byte)'a' && b <= (byte)'~'))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append($"%{b:X2}");
            }
            i++;
        }

        return sb.ToString();
    }

    static bool IsUnreservedOrSub(byte b) =>
        b is (byte)'!' or (byte)'$' or (>= (byte)'&' and <= (byte)'.') or (>= (byte)'0' and <= (byte)'9') or (byte)';' or (byte)'=' or (>= (byte)'A' and <= (byte)'Z') or (byte)'_' or (>= (byte)'a' and <= (byte)'z') or (byte)'~';

    static bool PctAt(byte[] s, int i) =>
        i + 2 < s.Length && s[i] == (byte)'%' && char.IsAsciiHexDigit((char)s[i + 1]) && char.IsAsciiHexDigit((char)s[i + 2]);

    static int TakeWhileClass(byte[] s, int i, Func<byte, bool> predicate)
    {
        while (i < s.Length)
        {
            if (PctAt(s, i))
            {
                i += 3;
            }
            else if (predicate(s[i]))
            {
                i++;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    static bool SegChar(byte b) => IsUnreservedOrSub(b) || b is (byte)':' or (byte)'@' or (byte)'/';

    static bool SegNcChar(byte b) => IsUnreservedOrSub(b) || b is (byte)'@';

    static bool FragmentChar(byte b) => IsUnreservedOrSub(b) || b is (byte)':' or (byte)'@' or (byte)'/' or (byte)'?';

    static bool UserinfoChar(byte b) => IsUnreservedOrSub(b) || b is (byte)':';

    static int? IpLiteral(byte[] s, int i)
    {
        if (i >= s.Length || s[i] != (byte)'[')
        {
            return null;
        }

        var close = Array.IndexOf(s, (byte)']', i);
        if (close < 0)
        {
            return null;
        }

        var inner = Encoding.ASCII.GetString(s, i + 1, close - i - 1);
        bool valid;
        if (inner.StartsWith('v') || inner.StartsWith('V'))
        {
            var parts = inner[1..].Split('.', 2);
            valid = parts.Length == 2 &&
                    parts[0].Length > 0 && parts[0].All(char.IsAsciiHexDigit) &&
                    parts[1].Length > 0 && parts[1].All(c => IsUnreservedOrSub((byte)c) || c == ':');
        }
        else
        {
            valid = System.Net.IPAddress.TryParse(inner, out var ip) &&
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                    !inner.Contains('%');
        }

        return valid ? close + 1 : null;
    }

    static RubyUri? SplitAbsolute(string value)
    {
        var s = Encoding.ASCII.GetBytes(value);
        if (s.Length == 0 || !char.IsAsciiLetter((char)s[0]))
        {
            return null;
        }

        var i = 1;
        while (i < s.Length && (char.IsAsciiLetterOrDigit((char)s[i]) || s[i] is (byte)'+' or (byte)'-' or (byte)'.'))
        {
            i++;
        }

        if (i >= s.Length || s[i] != (byte)':')
        {
            return null;
        }

        var scheme = value[..i].ToLowerInvariant();
        var restStart = i + 1;
        var tail = ParseQueryFragmentPositions(s, restStart);
        if (tail is null)
        {
            return null;
        }

        var hier = s[restStart..tail.Value.HierEnd];
        var uri = new RubyUri
        {
            Scheme = scheme,
            Query = tail.Value.Query is not null ? value[tail.Value.Query.Value.Start..tail.Value.Query.Value.End] : null,
            Fragment = tail.Value.Fragment is not null ? value[tail.Value.Fragment.Value.Start..tail.Value.Fragment.Value.End] : null
        };

        if (hier.StartsWith("//"u8))
        {
            var authority = ParseAuthority(value, restStart + 2, tail.Value.HierEnd);
            if (authority is null)
            {
                return null;
            }

            uri.Userinfo = authority.Userinfo;
            uri.Host = authority.Host;
            uri.Port = authority.Port;
            uri.Path = value[authority.End..tail.Value.HierEnd];

            var path = s[authority.End..tail.Value.HierEnd];
            if (path.Length > 0 && (path[0] != (byte)'/' || TakeWhileClass(s, authority.End, SegChar) != tail.Value.HierEnd))
            {
                return null;
            }
        }
        else if (hier.StartsWith("/"u8))
        {
            if (TakeWhileClass(s, restStart, SegChar) != tail.Value.HierEnd)
            {
                return null;
            }
            uri.Path = value[restStart..tail.Value.HierEnd];
        }
        else if (hier.Length > 0)
        {
            if (TakeWhileClass(s, restStart, SegChar) != tail.Value.HierEnd)
            {
                return null;
            }
            var opaque = value[restStart..tail.Value.HierEnd];
            if (uri.Query is not null)
            {
                opaque += "?" + uri.Query;
                uri.Query = null;
            }
            uri.Opaque = opaque;
        }
        else
        {
            uri.Path = string.Empty;
        }

        return uri;
    }

    record struct Tail(int HierEnd, (int Start, int End)? Query, (int Start, int End)? Fragment);

    static Tail? ParseQueryFragmentPositions(byte[] s, int start)
    {
        var hierEnd = s.Length;
        for (var idx = start; idx < s.Length; idx++)
        {
            if (s[idx] is (byte)'?' or (byte)'#')
            {
                hierEnd = idx;
                break;
            }
        }

        var i = hierEnd;
        (int Start, int End)? query = null;
        if (i < s.Length && s[i] == (byte)'?')
        {
            var qEnd = s.Length;
            for (var j = i + 1; j < s.Length; j++)
            {
                if (s[j] == (byte)'#')
                {
                    qEnd = j;
                    break;
                }
            }
            query = (i + 1, qEnd);
            i = qEnd;
        }

        (int Start, int End)? fragment = null;
        if (i < s.Length && s[i] == (byte)'#')
        {
            var fEnd = TakeWhileClass(s, i + 1, FragmentChar);
            if (fEnd != s.Length)
            {
                return null;
            }
            fragment = (i + 1, s.Length);
            i = s.Length;
        }

        return i == s.Length ? new Tail(hierEnd, query, fragment) : null;
    }

    sealed record Authority(string? Userinfo, string Host, ulong? Port, int End);

    static Authority? ParseAuthority(string value, int start, int limit)
    {
        var s = Encoding.ASCII.GetBytes(value)[..limit];
        var i = start;
        string? userinfo = null;
        var uiEnd = TakeWhileClass(s, i, UserinfoChar);
        if (uiEnd < s.Length && s[uiEnd] == (byte)'@')
        {
            userinfo = value[i..uiEnd];
            i = uiEnd + 1;
        }

        var hostEnd = IpLiteral(s, i) ?? TakeWhileClass(s, i, IsUnreservedOrSub);
        var host = value[i..hostEnd];
        var end = hostEnd;
        ulong? port = null;

        if (end < s.Length && s[end] == (byte)':')
        {
            var digitsEnd = end + 1;
            while (digitsEnd < s.Length && char.IsAsciiDigit((char)s[digitsEnd]))
            {
                digitsEnd++;
            }
            var digits = value[(end + 1)..digitsEnd];
            port = digits.Length == 0 ? null : (ulong.TryParse(digits, out var p) ? p : ulong.MaxValue);
            end = digitsEnd;
        }

        if (end < s.Length && s[end] != (byte)'/')
        {
            return null;
        }

        return new Authority(userinfo, host, port, end);
    }

    static RubyUri? SplitRelative(string value)
    {
        var s = Encoding.ASCII.GetBytes(value);
        var tail = ParseQueryFragmentPositions(s, 0);
        if (tail is null)
        {
            return null;
        }

        var hier = s[..tail.Value.HierEnd];
        bool valid;
        if (hier.StartsWith("//"u8))
        {
            var authority = ParseAuthority(value, 2, tail.Value.HierEnd);
            valid = authority is not null && TakeWhileClass(s, authority.End, SegChar) == tail.Value.HierEnd;
        }
        else if (hier.StartsWith("/"u8) || hier.Length == 0)
        {
            valid = TakeWhileClass(s, 0, SegChar) == tail.Value.HierEnd;
        }
        else
        {
            var first = TakeWhileClass(s, 0, SegNcChar);
            valid = first > 0 && (first == tail.Value.HierEnd || (first < s.Length && s[first] == (byte)'/' && TakeWhileClass(s, first, SegChar) == tail.Value.HierEnd));
        }

        if (!valid)
        {
            return null;
        }

        return new RubyUri
        {
            Path = value[..tail.Value.HierEnd],
            Query = tail.Value.Query is not null ? value[tail.Value.Query.Value.Start..tail.Value.Query.Value.End] : null,
            Fragment = tail.Value.Fragment is not null ? value[tail.Value.Fragment.Value.Start..tail.Value.Fragment.Value.End] : null
        };
    }
}
