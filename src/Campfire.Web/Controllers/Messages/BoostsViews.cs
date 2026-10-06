namespace Campfire.Web.Helpers;

// reference/app/views/messages/boosts/{index,new}.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>messages/boosts/index</c>: the message's boosts.</summary>
    [ErbTemplate("messages/boosts/index.html.erb.cs")]
    public partial void MessagesBoostsIndex(HtmlWriter w, MessageView message);

    /// <summary><c>messages/boosts/new</c>: the boost form, with <paramref name="user"/> (<c>Current.user</c>) as the booster.</summary>
    [ErbTemplate("messages/boosts/new.html.erb.cs")]
    public partial void MessagesBoostsNew(HtmlWriter w, MessageView message, MessageUser user);
}
#pragma warning restore IDE0060
