using System.Text;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.GlobalId;

/// <summary>
/// A parsed GlobalID URI (<c>URI::GID</c>, globalid 1.4.0): <c>gid://&lt;app&gt;/&lt;Model&gt;/&lt;id&gt;[?params]</c>.
/// <c>URI::GID.parse</c> splits it with Ruby 4.0's RFC 3986 parser, so the app is any RFC 3986
/// host (a reg-name with sub-delims such as <c>&amp;</c>, an IP literal), userinfo and port are
/// accepted and dropped, and the model id is <c>CGI.unescape</c>d. A string it rejects is null,
/// which is what <c>GlobalID.parse</c> returns after rescuing <c>URI::Error</c>
/// (<c>Vectors/parse.json</c> in the tests holds the reference's answers).
/// </summary>
public sealed partial record GlobalId(string App, string ModelName, string Id)
{
    public const string DefaultApp = "campfire";

    // URI::GID::COMPOSITE_MODEL_ID_MAX_SIZE
    const int compositeModelIdMaxSize = 20;

    /// <summary>
    /// True when the model id isn't one path segment (an array in Rails, possibly empty).
    /// globalid's locator finds nothing for it, and <c>find</c> does not cast it with <c>to_i</c>.
    /// </summary>
    public bool Composite { get; init; }

    // URI::RFC3986_Parser::RFC3986_URI (uri 1.1.1) for a `gid` URI with an authority: only that
    // form has the host URI::GID requires. Possessive quantifiers are atomic groups here, and
    // the ipv4 alternative of the host is left out because a reg-name matches the same text.
    const string hex = "[0-9A-Fa-f]";
    const string decOctet = "(?:[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5]|[0-9])";
    const string ipv4 = decOctet + @"\." + decOctet + @"\." + decOctet + @"\." + decOctet;
    const string h16 = hex + "{1,4}";
    const string ls32 = "(?:" + h16 + ":" + h16 + "|" + ipv4 + ")";
    const string ipv6 =
        "(?:(?:" + h16 + ":){6}" + ls32 +
        "|::(?:" + h16 + ":){5}" + ls32 +
        "|" + hex + "{1,4}?::(?:" + h16 + ":){4}" + ls32 +
        "|(?:(?:" + h16 + ":)?" + h16 + ")?::(?:" + h16 + ":){3}" + ls32 +
        "|(?:(?:" + h16 + ":){0,2}" + h16 + ")?::(?:" + h16 + ":){2}" + ls32 +
        "|(?:(?:" + h16 + ":){0,3}" + h16 + ")?::" + h16 + ":" + ls32 +
        "|(?:(?:" + h16 + ":){0,4}" + h16 + ")?::" + ls32 +
        "|(?:(?:" + h16 + ":){0,5}" + h16 + ")?::" + h16 +
        "|(?:(?:" + h16 + ":){0,6}" + h16 + ")?::)";
    const string ipvFuture = "v(?>" + hex + @"+)\.(?>[!$&-.0-9:;=A-Z_a-z~]+)";
    const string pct = "%" + hex + hex;
    const string regName = "(?>(?:" + pct + "|[!$&-.0-9;=A-Z_a-z~])*)";
    const string userInfo = "(?>(?:" + pct + "|[!$&-.0-9:;=A-Z_a-z~])*)";
    const string seg = "(?:" + pct + "|[!$&-.0-9:;=@A-Z_a-z~/])";
    const string fragment = "(?>(?:" + pct + "|[!$&-.0-9:;=@A-Z_a-z~/?])*)";

    [GeneratedRegex(
        @"\Agid://(?:" + userInfo + "@)?(?<host>" + @"\[(?:" + ipv6 + "|" + ipvFuture + @")\]|" + regName + ")(?::(?>[0-9]*))?" +
        "(?<path>(?:/(?>" + seg + "*))?)" + @"(?:\?(?<query>(?>[^#]*)))?(?:#" + fragment + @")?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex GidUri();

    // URI::Generic#query= raises for a percent sign followed by two non-hex characters
    [GeneratedRegex("%[^0-9A-Fa-f][^0-9A-Fa-f]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidPercentEscape();

    public static GlobalId Create(string modelName, object id, string app = DefaultApp) =>
        new(app, modelName, id.ToString()!);

    /// <summary>Parses a GID string the way <c>GlobalID.parse</c> accepts a <c>gid://</c> URI.</summary>
    public static GlobalId? Parse(string? gid)
    {
        if (string.IsNullOrEmpty(gid))
        {
            return null;
        }

        // RFC3986_Parser#split requires uri.ascii_only? and raises URI::InvalidURIError otherwise
        foreach (var c in gid)
        {
            if (c > 127)
            {
                return null;
            }
        }

        var match = GidUri().Match(gid);
        // URI::GID#check_host: the app can't be blank
        if (!match.Success || match.Groups["host"].Length == 0)
        {
            return null;
        }

        if (match.Groups["query"] is { Success: true } query
            && InvalidPercentEscape().IsMatch(query.Value.Replace("\t", "").Replace("\r", "").Replace("\n", "")))
        {
            return null;
        }

        // set_model_components: path.split('/', 3), then a blank model name or model id raises
        var path = match.Groups["path"].Value.Split('/', 3);
        if (path.Length < 3 || path[1].Length == 0 || path[2].Length == 0)
        {
            return null;
        }

        // model_id.split("/", 20).reject(&:blank?), and a part holding a '/' raises
        var ids = path[2].Split('/', compositeModelIdMaxSize).Where(part => part.Length > 0).ToArray();
        if (ids.Any(part => part.Contains('/', StringComparison.Ordinal)))
        {
            return null;
        }

        var unescaped = ids.Select(CgiUnescape).ToArray();
        return new GlobalId(match.Groups["host"].Value, path[1], unescaped.Length == 1 ? unescaped[0] : string.Join('/', unescaped))
        {
            Composite = unescaped.Length != 1,
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
