namespace Campfire.Web.Helpers;

// reference/app/views/messages/_*.html.erb and messages/boosts/_*.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>messages/_message</c>: a message with its author, actions, presentation and boosts, or
    /// <c>messages/_unrenderable</c> in its place.
    /// </summary>
    [ErbTemplate("messages/_message.html.erb.cs")]
    public partial void MessagesMessage(HtmlWriter w, MessageView message);

    /// <summary><c>messages/_actions</c>: the options menu; <paramref name="url"/> is the message's permalink.</summary>
    [ErbTemplate("messages/_actions.html.erb.cs")]
    public partial void MessagesActions(HtmlWriter w, MessageView message, string url);

    /// <summary><c>messages/_presentation</c>: the body as <c>message_presentation</c> shows it.</summary>
    [ErbTemplate("messages/_presentation.html.erb.cs")]
    public partial void MessagesPresentation(HtmlWriter w, MessageView message);

    /// <summary>
    /// <c>messages/_template</c>: the client-side template the composer fills in for
    /// <paramref name="user"/>'s (<c>Current.user</c>'s) pending messages.
    /// </summary>
    [ErbTemplate("messages/_template.html.erb.cs")]
    public partial void MessagesTemplate(HtmlWriter w, MessageUser user);

    /// <summary><c>messages/_unrenderable</c>.</summary>
    [ErbTemplate("messages/_unrenderable.html.erb.cs")]
    public partial void MessagesUnrenderable(HtmlWriter w);

    /// <summary><c>messages/boosts/_boosts</c>: a message's boosts and its add-a-boost link.</summary>
    [ErbTemplate("messages/boosts/_boosts.html.erb.cs")]
    public partial void MessagesBoostsBoosts(HtmlWriter w, MessageView message);

    /// <summary><c>messages/boosts/_boost</c>.</summary>
    [ErbTemplate("messages/boosts/_boost.html.erb.cs")]
    public partial void MessagesBoostsBoost(HtmlWriter w, BoostView boost);
}
#pragma warning restore IDE0060
