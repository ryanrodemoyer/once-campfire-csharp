using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Web.Helpers;

// reference/app/views/messages/by_bots/{index,show}.json.jbuilder: the bot API's messages, each
// the messages/_message.json partial. The partials' `json.cache!` isn't modeled (M02's gap).
public partial class View
{
    /// <summary><c>messages/by_bots/index.json</c>: <c>json.array! @messages, partial: "messages/message"</c>.</summary>
    public string MessagesByBotsIndex(IReadOnlyList<MessageView> messages) =>
        RailsJson.Encode(new JsonArray([.. messages.Select(message => (JsonNode)MessageJsonObject(message))]));

    /// <summary><c>messages/by_bots/show.json</c>: <c>json.partial! "messages/message"</c>.</summary>
    public string MessagesByBotsShow(MessageView message) => MessageJson(message);
}
