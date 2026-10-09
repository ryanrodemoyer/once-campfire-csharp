using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// messages#index: GET /rooms/:room_id/messages and GET /messages.
public static partial class Routes
{
    static partial void MessagesIndex(ref RequestDelegate? handler) => handler = MessagesIndexController.Index;
}
