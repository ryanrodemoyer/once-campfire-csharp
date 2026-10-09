namespace Campfire.Web.Helpers;

// reference/app/views/pwa/service_worker.js (not ERB: the file is served as written).
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>pwa/service_worker.js</c>: push notifications and notification clicks.</summary>
    [ErbTemplate("pwa/service_worker.js.erb.cs")]
    public partial void PwaServiceWorker(HtmlWriter w);
}
#pragma warning restore IDE0060
