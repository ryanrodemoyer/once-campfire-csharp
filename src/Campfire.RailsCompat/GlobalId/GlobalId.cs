using System.Text;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.GlobalId;

/// <summary>
/// A parsed GlobalID URI (<c>URI::GID</c>, globalid 1.3.0): <c>gid://&lt;app&gt;/&lt;Model&gt;/&lt;id&gt;[?params]</c>.
/// The app is an RFC2396 host, the path segments are RFC2396 path segments (<c>:</c> included, so
/// <c>Rooms::Open</c> parses), and the model id is <c>CGI.unescape</c>d. Query parameters
/// (e.g. <c>?expires_in</c>) and a fragment are accepted and dropped. A string
/// <c>URI::GID.parse</c> rejects — a trailing newline, an underscore in the host, a non-URI
/// character — is null, which is what <c>GlobalID.parse</c> returns after rescuing
/// <c>URI::Error</c>.
/// </summary>
public sealed partial record GlobalId(string App, string ModelName, string Id)
{
    public const string DefaultApp = "campfire";

    /// <summary>
    /// True when the model id is more than one path segment. Rails then holds an array, and
    /// <c>find</c> does not cast it with <c>to_i</c>.
    /// </summary>
    public bool Composite { get; init; }

    // RFC2396 via URI::GID (globalid 1.3.0). The scheme must be the lowercase `gid`
    // (`check_scheme`). Userinfo (`gid://cao@fire/...`) is accepted and dropped; the app is the
    // host. Underscore is legal in the query and not in the host. The query is dropped, and
    // URI.split accepts any byte there, including controls (`gid://app/User/1?\x7Fx`).
    [GeneratedRegex(
        @"\Agid://(?:(?:[A-Za-z0-9\-_.!~*'();:&=+$,]|%[0-9A-Fa-f]{2})*@)?(?<host>(?:[A-Za-z0-9\-.]|%[0-9A-Fa-f]{2})+)(?<path>(?:/(?:[A-Za-z0-9\-_.!~*'():@&=+$,]|%[0-9A-Fa-f]{2})*)+)(?:\?[\s\S]*)?(?:#(?<fragment>[^\s#]*))?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex GidUri();

    public static GlobalId Create(string modelName, object id, string app = DefaultApp) =>
        new(app, modelName, id.ToString()!);

    /// <summary>Parses a GID string the way <c>GlobalID.parse</c> accepts a <c>gid://</c> URI.</summary>
    public static GlobalId? Parse(string? gid)
    {
        if (string.IsNullOrEmpty(gid))
        {
            return null;
        }

        // URI::RFC3986_PARSER.split requires uri.ascii_only? and raises URI::InvalidURIError otherwise
        for (var i = 0; i < gid.Length; i++)
        {
            if (gid[i] > 127)
            {
                return null;
            }
        }

        var match = GidUri().Match(gid);
        if (!match.Success)
        {
            return null;
        }

        var segments = match.Groups["path"].Value.Split('/');
        // path begins with '/', so segments[0] is empty
        if (segments.Length < 3 || segments[1].Length == 0)
        {
            return null;
        }

        var rawIds = segments[2..];
        // split(delimiter, 20) folds the tail into the last part, and a '/' there fails validation
        if (rawIds.Length > 20)
        {
            return null;
        }

        var ids = rawIds.Where(part => part.Length > 0).Select(CgiUnescape).ToArray();
        if (ids.Length == 0)
        {
            return null;
        }

        return new GlobalId(match.Groups["host"].Value, segments[1], ids.Length == 1 ? ids[0] : string.Join('/', ids))
        {
            Composite = ids.Length > 1,
        };
    }

    /// <summary><c>CGI.unescape</c>: <c>+</c> is a space, then <c>%HH</c> bytes are read as UTF-8.</summary>
    static string CgiUnescape(string value)
    {
        if (value.IndexOf('%') < 0 && value.IndexOf('+') < 0)
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>
    /// <c>GlobalID#to_param</c>: URL-safe Base64 without padding, as used in Turbo stream names.
    /// </summary>
    public string ToParam() =>
        RubyBase64.UrlSafeEncode(Encoding.UTF8.GetBytes(ToString()), padding: false);

    /// <summary>Decodes a GID from its <c>to_param</c> representation.</summary>
    public static GlobalId? FromParam(string? param)
    {
        if (string.IsNullOrEmpty(param)) return null;
        var bytes = RubyBase64.UrlSafeDecode(param);
        if (bytes is null) return null;
        try
        {
            var str = Encoding.UTF8.GetString(bytes);
            return Parse(str);
        }
        catch
        {
            return null;
        }
    }

    public override string ToString() => $"gid://{App}/{ModelName}/{Id}";
}
