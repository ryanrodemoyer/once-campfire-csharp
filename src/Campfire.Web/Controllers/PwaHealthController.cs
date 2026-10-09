using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Formatting;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Rails::HealthController</c> (railties <c>health_controller.rb</c>): <c>GET /up</c>. It is
/// <c>ActionController::Base</c>, not the app controller, so it has no authentication, no browser
/// block and no version headers. <c>rescue_from Exception</c> answers red.
/// </summary>
public sealed class RailsHealthController : Controller
{
    static readonly ControllerCallbacks<RailsHealthController> Chain = BaseCallbacks.For<RailsHealthController>()
        .RescueFrom<Exception>((controller, _) => controller.RenderDown());

    /// <summary><c>GET /up</c>.</summary>
    public static readonly RequestDelegate Show = Action(Chain, controller => controller.RenderUp());

    // render_up
    void RenderUp() => RenderStatus("green", "up", 200);

    // render_down
    void RenderDown() => RenderStatus("red", "down", 500);

    // respond_to html, then json. `render html:` is the doctype string; JSON is Time.current.iso8601.
    void RenderStatus(string color, string status, int code)
    {
        var format = RespondTo(MimeType.Html, MimeType.Json);
        if (format == MimeType.Html)
        {
            Render($"<!DOCTYPE html><html><body style=\"background-color: {color}\"></body></html>", MimeType.Html.Value, code);
            return;
        }

        var json = RailsJson.Encode(new JsonObject
        {
            ["status"] = status,
            ["timestamp"] = TimeFormats.Iso8601(Now),
        });
        Render(json, MimeType.Json.Value, code);
    }
}
