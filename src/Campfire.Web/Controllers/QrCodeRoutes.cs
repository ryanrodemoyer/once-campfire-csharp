using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// QrCodeController (`get "qr_code/:id", to: "qr_code#show"`).
public static partial class Routes
{
    static partial void QrCodeShow(ref RequestDelegate? handler) => handler = QrCodeController.Show;
}
