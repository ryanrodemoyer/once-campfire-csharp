using System.Globalization;
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

    /// <summary><c>UnfurlLinksController::DEADLINE</c>: 10 seconds.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary><c>POST /unfurl_link</c>: fetch metadata and render JSON or 204 No Content.</summary>
    public static readonly RequestDelegate Create = Action(Chain, async c =>
    {
        var url = c.Params.Require("url").ToString()!;
        var deadline = c.App.UnfurlDeadline ?? Deadline;
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        deadlineCts.CancelAfter(deadline);
        try
        {
            var json = await OpenGraphMetadata.UnfurlAsync(c.App.OpenGraphFetch, url, deadlineCts.Token).ConfigureAwait(false);
            if (json is not null)
            {
                c.Render(json, MimeType.Json.Value);
            }
            else
            {
                c.Head(204);
            }
        }
        catch (OperationCanceledException) when (!c.RequestAborted.IsCancellationRequested)
        {
            c.App.OpenGraphFetch?.Warn?.Invoke($"Gave up unfurling {url} after {deadline.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} seconds");
            c.Head(204);
        }
    });
}
