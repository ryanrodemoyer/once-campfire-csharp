using System.Globalization;
using System.Text;

namespace Campfire.Web.QrCode;

// QrCodeController#show (reference/app/controllers/qr_code_controller.rb):
// `RQRCode::QRCode.new(url).as_svg(viewbox: true, fill: :white, color: :black)`, rqrcode 3.2.0's
// Rect output over rqrcode_core 2.1.0's matrix. Golden vectors: vectors/rqrcode.json.
public static class QrCodeSvg
{
    const int moduleSize = 11;

    // The SVG for the binary string `Base64.urlsafe_decode64` returns, or null where rqrcode raises
    // because the data doesn't fit a version 40 code.
    public static string? Render(ReadOnlySpan<byte> data)
    {
        var code = QrCodeMatrix.Create(data);
        return code is null ? null : Render(code.Modules);
    }

    static string Render(bool[][] modules)
    {
        var dimension = modules.Length * moduleSize;
        var svg = new StringBuilder(256 + modules.Length * modules.Length * 30);
        svg.Append("""<?xml version="1.0" standalone="yes"?>""");
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg version="1.1" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:ev="http://www.w3.org/2001/xml-events" viewBox="0 0 {dimension} {dimension}" shape-rendering="crispEdges">""");
        svg.Append(CultureInfo.InvariantCulture, $"""<rect width="{dimension}" height="{dimension}" x="0" y="0" fill="white"/>""");
        for (var row = 0; row < modules.Length; row++)
        {
            for (var col = 0; col < modules[row].Length; col++)
            {
                if (modules[row][col])
                {
                    svg.Append(CultureInfo.InvariantCulture,
                        $"""<rect width="{moduleSize}" height="{moduleSize}" x="{col * moduleSize}" y="{row * moduleSize}" fill="black"/>""");
                }
            }
        }

        svg.Append("</svg>");
        return svg.ToString();
    }
}
