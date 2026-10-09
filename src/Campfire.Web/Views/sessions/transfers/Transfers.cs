namespace Campfire.Web.Helpers;

// reference/app/views/sessions/transfers/show.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>sessions/transfers/show</c>: a form that puts the page's own URL as soon as it loads.</summary>
    [ErbTemplate("sessions/transfers/show.html.erb.cs")]
    public partial void SessionsTransfersShow(HtmlWriter w);
}
#pragma warning restore IDE0060
