namespace Campfire.Web.Helpers;

// reference/app/views/messages/index.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>messages/index</c>: the page of messages, with no layout.</summary>
    [ErbTemplate("messages/index.html.erb.cs")]
    public partial void MessagesIndex(HtmlWriter w, IReadOnlyList<MessageView> messages);
}
#pragma warning restore IDE0060
