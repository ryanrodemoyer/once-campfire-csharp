using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// RoomsController's index and show (`resources :rooms` and `/rooms/:room_id/@:message_id`).
public static partial class Routes
{
    static partial void RoomsIndex(ref RequestDelegate? handler) => handler = RoomsController.Index;

    static partial void RoomsShow(ref RequestDelegate? handler) => handler = RoomsController.Show;
}
