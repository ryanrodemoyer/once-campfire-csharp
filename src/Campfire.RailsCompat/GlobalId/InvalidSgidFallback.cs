using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.GlobalId;

/// <summary>
/// Campfire's unverified SGID fallback (<c>reference/lib/rails_ext/action_text_attachables.rb</c>).
/// Mentions use ActionText attachments, which are signed. If someone rotates SECRET_KEY_BASE,
/// existing attachments become invalid. This allows ignoring invalid signatures for User
/// attachments only.
/// </summary>
public static partial class InvalidSgidFallback
{
    public static readonly string[] PermittedModels = ["User"];

    [GeneratedRegex(@"gid://campfire/([^/]+)/(\d+)", RegexOptions.Compiled)]
    private static partial Regex MarshaledGidRegex();

    /// <summary>
    /// Attempts to extract a <see cref="GlobalId"/> from a possibly expired or invalid SGID,
    /// permitting only the User model.
    /// </summary>
    /// <param name="sgid">The raw signed GlobalID string.</param>
    /// <returns>The resolved <see cref="GlobalId"/> if User, or <c>null</c> if not User or unresolvable.</returns>
    public static GlobalId? AttachableFromPossiblyExpiredSgid(string? sgid)
    {
        if (string.IsNullOrEmpty(sgid))
        {
            return null;
        }

        var dashIdx = sgid.IndexOf("--", StringComparison.Ordinal);
        var message = dashIdx >= 0 ? sgid[..dashIdx] : sgid;
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var decoded = DecodeBase64(message);
        var utf8Str = Encoding.UTF8.GetString(decoded);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(utf8Str);
        }
        catch (JsonException)
        {
            throw;
        }

        if (root is JsonArray)
        {
            throw new InvalidOperationException("TypeError: no implicit conversion of String into Integer");
        }

        if (root is not JsonObject map)
        {
            return null;
        }

        if (!map.TryGetPropertyValue("_rails", out var railsNode) || railsNode is null)
        {
            return null;
        }

        if (railsNode is not JsonObject railsObj)
        {
            throw new InvalidOperationException("TypeError: dig");
        }

        string? gidStr = null;
        if (railsObj.TryGetPropertyValue("data", out var dataNode) && dataNode is not null)
        {
            if (dataNode is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                gidStr = s;
            }
        }
        else if (railsObj.TryGetPropertyValue("message", out var msgNode) && msgNode is not null)
        {
            if (msgNode is JsonValue jv && jv.TryGetValue<string>(out var msgStr))
            {
                var msgBytes = DecodeBase64(msgStr);
                var match = MarshaledGidRegex().Match(Encoding.UTF8.GetString(msgBytes));
                if (match.Success)
                {
                    gidStr = match.Value;
                }
            }
            else
            {
                throw new InvalidOperationException("NoMethodError: unpack1");
            }
        }

        if (string.IsNullOrEmpty(gidStr))
        {
            return null;
        }

        var parsed = GlobalId.Parse(gidStr);
        if (parsed is null)
        {
            return null;
        }

        // Only User is permitted with an invalid or expired signature
        if (!PermittedModels.Contains(parsed.ModelName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        // Returns normalized to campfire app
        return GlobalId.Create(parsed.ModelName, parsed.Id);
    }

    private static byte[] DecodeBase64(string message)
    {
        return RubyBase64.StrictDecode(message)
            ?? RubyBase64.UrlSafeDecode(message)
            ?? throw new FormatException("ArgumentError: invalid base64");
    }
}
