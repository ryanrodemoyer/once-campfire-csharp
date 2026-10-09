using Campfire.RailsCompat.Crypto;
using Campfire.Web.Pipeline;
using Campfire.Web.QrCode;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>QrCodeController</c> (reference/app/controllers/qr_code_controller.rb): a public SVG of the
/// URL carried in the path, cached for a year.
/// </summary>
public sealed class QrCodeController : ApplicationController
{
    // 1.year: a Julian-Gregorian year of 365.2425 days.
    const int oneYear = 31_556_952;

    static readonly ControllerCallbacks<QrCodeController> Chain = Callbacks.For<QrCodeController>()
        .AllowUnauthenticatedAccess();

    /// <summary><c>GET /qr_code/:id</c>.</summary>
    public static readonly RequestDelegate Show = Action(Chain, controller => controller.RenderCode());

    // The block runs before formats are narrowed, so a .json request still gets the HTML page.
    protected override ValueTask RenderIncompatibleBrowserAsync() => PwaIncompatibleBrowser.RenderAsync(this);

    // Base64.urlsafe_decode64 raises ArgumentError on bad input; rqrcode raises when the data
    // doesn't fit a version 40 code. Both are 500s. `render plain:, content_type: "image/svg+xml"`.
    void RenderCode()
    {
        var id = Params["id"] as string ?? "";
        var data = RubyBase64.UrlSafeDecode(id) ?? throw new InvalidOperationException("invalid base64");
        var svg = QrCodeSvg.Render(data) ?? throw new InvalidOperationException("data doesn't fit a QR code");
        ExpiresIn(TimeSpan.FromSeconds(oneYear), isPublic: true);
        Render(svg, "image/svg+xml");
    }
}
