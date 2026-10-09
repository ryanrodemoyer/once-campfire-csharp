using System.Buffers;
using System.Text;
using Campfire.Data.Queries;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>PwaController</c> (reference/app/controllers/pwa_controller.rb): the installable app's
/// manifest and service worker, at stable root URLs. Both are public and skip forgery
/// protection, because a page loads <c>/service-worker.js</c> as a script.
/// </summary>
public sealed class PwaController : ApplicationController
{
    static readonly ControllerCallbacks<PwaController> Chain = Callbacks.For<PwaController>()
        .AllowUnauthenticatedAccess()
        .SkipForgeryProtection();

    /// <summary><c>GET /webmanifest</c>: <c>pwa/manifest.json</c> when the request format is JSON.</summary>
    public static readonly RequestDelegate Manifest = Action(Chain, c => c.ManifestAsync());

    /// <summary><c>GET /service-worker</c>: <c>pwa/service_worker.js</c> when the request format is JS.</summary>
    public static readonly RequestDelegate ServiceWorker = Action(Chain, c => c.RenderServiceWorker());

    // The block runs before formats are narrowed, so /webmanifest.json still gets the HTML page.
    protected override ValueTask RenderIncompatibleBrowserAsync() => PwaIncompatibleBrowser.RenderAsync(this);

    // Implicit render: only the template for this format. Anything else falls through to
    // default_render (406 for an HTML browser navigation, 204 otherwise).
    async ValueTask ManifestAsync()
    {
        // */* (and an Accept list that contains it) still finds manifest.json.
        if (!Accepts(MimeType.Json))
        {
            return;
        }

        var account = await ReadAsync(Accounts.First).ConfigureAwait(false);
        var body = RenderString(writer => View().PwaManifest(writer, account?.Name, account?.UpdatedAt));
        Render(body, MimeType.Json.Value);
    }

    void RenderServiceWorker()
    {
        if (!Accepts(MimeType.Js))
        {
            return;
        }

        Render(RenderString(writer => View().PwaServiceWorker(writer)), MimeType.Js.Value);
    }

    // Implicit lookup matches the template's format, or */* anywhere in request.formats.
    bool Accepts(MimeType template) => Request.Formats.Any(format => format == template || format.IsAll);

    View View() => new()
    {
        Assets = App.RequireAssets(),
        Origin = UrlBase,
    };

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
