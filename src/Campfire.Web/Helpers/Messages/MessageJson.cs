using System.Text.Json.Nodes;
using Campfire.Data.Records;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Formatting;
using Campfire.RichText.Attachments;

namespace Campfire.Web.Helpers;

// reference/app/views/messages/_message.json.jbuilder, messages/boosts/_boost.json.jbuilder and
// the users/_user.json.jbuilder partial both render. Keys keep jbuilder's order; the JSON is
// encoded as Rails encodes it (HTML entities escaped).
public partial class View
{
    /// <summary>The <c>messages/_message.json</c> partial, encoded.</summary>
    public string MessageJson(MessageView message) => RailsJson.Encode(MessageJsonObject(message));

    /// <summary>The <c>messages/boosts/_boost.json</c> partial, encoded.</summary>
    public string BoostJson(BoostView boost) => RailsJson.Encode(BoostJsonObject(boost));

    /// <summary>
    /// <c>messages/_message.json</c>: the id, time, body as plain text and HTML (<c>body.to_s</c>),
    /// creator, room and URL.
    /// </summary>
    public JsonObject MessageJsonObject(MessageView message) => new()
    {
        ["id"] = message.Id,
        ["created_at"] = TimeFormats.AsJson(message.CreatedAt),
        ["body"] = new JsonObject
        {
            ["plain_text"] = message.PlainTextBody,
            ["html"] = RichTextToS(message.Body),
        },
        ["creator"] = UserJsonObject(message.Creator),
        ["room"] = new JsonObject { ["id"] = message.Room.Id },
        ["url"] = Routes.RoomMessageUrl(Origin, message.Room.Id, message.Id),
    };

    /// <summary><c>messages/boosts/_boost.json</c>.</summary>
    public JsonObject BoostJsonObject(BoostView boost) => new()
    {
        ["id"] = boost.Id,
        ["content"] = boost.Content,
        ["created_at"] = TimeFormats.AsJson(boost.CreatedAt),
        ["booster"] = UserJsonObject(boost.Booster),
        ["message"] = new JsonObject
        {
            ["id"] = boost.MessageId,
            ["url"] = Routes.RoomMessageUrl(Origin, boost.RoomId, boost.MessageId),
        },
    };

    /// <summary>
    /// <c>json.creator user, partial: "users/user", as: :user</c>: the id, name, role and avatar
    /// URL. jbuilder renders a partial for a nil object as an empty array.
    /// </summary>
    public JsonNode UserJsonObject(MessageUser? user) => user is null ? new JsonArray() : new JsonObject
    {
        ["id"] = user.Id,
        ["name"] = user.Name,
        ["role"] = RoleName(user.Role),
        ["avatar_url"] = Routes.FreshUserAvatarUrl(Origin, user.AvatarToken, user.UpdatedAt),
    };

    // `message.body.to_s`: the rendered rich text in its layout, "" without a body.
    string RichTextToS(string? body)
    {
        if (body is null)
        {
            return "";
        }
        var context = RichTextContext ?? throw new InvalidOperationException("View.RichTextContext isn't set");
        return RichTextRenderer.RenderWithLayout(RichTextRenderer.Load(body), context);
    }

    static string RoleName(UserRole role) => role switch
    {
        UserRole.Member => "member",
        UserRole.Administrator => "administrator",
        UserRole.Bot => "bot",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
