namespace Campfire.Web.Helpers;

// reference/app/views/messages/boosts/by_bots/show.json.jbuilder
public partial class View
{
    /// <summary><c>messages/boosts/by_bots/show.json</c>: <c>json.partial! "messages/boosts/boost"</c>.</summary>
    public string MessagesBoostsByBotsShow(BoostView boost) => BoostJson(boost);
}
