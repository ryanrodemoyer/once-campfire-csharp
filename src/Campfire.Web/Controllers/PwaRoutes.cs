using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// PwaController and Rails::HealthController (`get "webmanifest"`, `get "service-worker"`, `get "up"`).
public static partial class Routes
{
    static partial void PwaManifest(ref RequestDelegate? handler) => handler = PwaController.Manifest;

    static partial void PwaServiceWorker(ref RequestDelegate? handler) => handler = PwaController.ServiceWorker;

    static partial void RailsHealthShow(ref RequestDelegate? handler) => handler = RailsHealthController.Show;
}
