using Campfire.Jobs.OpenGraph;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>UnfurlLinksController</c> (reference/app/controllers/unfurl_links_controller.rb):
/// fetches Open Graph metadata for a given URL and returns it as JSON.
/// </summary>
public sealed class UnfurlLinksController : ApplicationController
{
    static readonly ControllerCallbacks<UnfurlLinksController> Chain = Callbacks.For<UnfurlLinksController>();

    /// <summary><c>POST /unfurl_link</c>: fetch metadata and render JSON or 204 No Content.</summary>
    public static readonly RequestDelegate Create = Action(Chain, async c =>
    {
        var url = c.Params.Require("url").ToString()!;
        var json = await OpenGraphMetadata.UnfurlAsync(c.App.OpenGraphFetch, url, c.RequestAborted).ConfigureAwait(false);
        if (json is not null)
        {
            c.Render(json, MimeType.Json.Value);
        }
        else
        {
            c.Head(204);
        }
    });
}
