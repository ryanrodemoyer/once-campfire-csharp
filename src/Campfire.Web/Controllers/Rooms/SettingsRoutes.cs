using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// rooms#destroy, the delete button on a room's settings page.
public static partial class Routes
{
    static partial void RoomsDestroy(ref RequestDelegate? handler) => handler = RoomsDestroyController.Destroy;
}
