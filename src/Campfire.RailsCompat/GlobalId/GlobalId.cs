using System.Text;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.GlobalId;

/// <summary>
/// A parsed GlobalID URI (<c>URI::GID</c>): <c>gid://&lt;app&gt;/&lt;Model&gt;/&lt;id&gt;[?params]</c>.
/// Query parameters (e.g. <c>?expires_in</c>) are dropped.
/// </summary>
public sealed record GlobalId(string App, string ModelName, string Id)
{
    public const string DefaultApp = "campfire";

    public static GlobalId Create(string modelName, object id, string app = DefaultApp) =>
        new(app, modelName, id.ToString()!);

    /// <summary>Parses a GID string: <c>gid://&lt;app&gt;/&lt;Model&gt;/&lt;id&gt;[?params]</c>.</summary>
    public static GlobalId? Parse(string? gid)
    {
        if (string.IsNullOrEmpty(gid) || !gid.StartsWith("gid://", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = gid["gid://".Length..];
        var queryIdx = rest.IndexOf('?');
        if (queryIdx >= 0)
        {
            rest = rest[..queryIdx];
        }

        var slash1 = rest.IndexOf('/');
        if (slash1 <= 0) return null;
        var app = rest[..slash1];

        var path = rest[(slash1 + 1)..];
        var slash2 = path.IndexOf('/');
        if (slash2 <= 0) return null;
        var modelName = path[..slash2];
        var id = path[(slash2 + 1)..];

        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(modelName) || string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new GlobalId(app, modelName, id);
    }

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
