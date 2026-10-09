using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// rooms/refreshes#show: GET /rooms/:room_id/refresh.
public static partial class Routes
{
    static partial void RoomsRefreshesShow(ref RequestDelegate? handler) => handler = RoomsRefreshesController.Show;
}
