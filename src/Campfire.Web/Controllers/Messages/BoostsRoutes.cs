using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Messages::BoostsController (`resources :boosts` under messages). It has no edit, show or update,
// so those routes raise AbstractController::ActionNotFound, a 404.
public static partial class Routes
{
    static partial void MessagesBoostsIndex(ref RequestDelegate? handler) => handler = MessagesBoostsController.Index;

    static partial void MessagesBoostsNew(ref RequestDelegate? handler) => handler = MessagesBoostsController.New;

    static partial void MessagesBoostsCreate(ref RequestDelegate? handler) => handler = MessagesBoostsController.Create;

    static partial void MessagesBoostsDestroy(ref RequestDelegate? handler) => handler = MessagesBoostsController.Destroy;

    static partial void MessagesBoostsEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "Messages::BoostsController");

    static partial void MessagesBoostsShow(ref RequestDelegate? handler) => handler = ActionNotFound("show", "Messages::BoostsController");

    static partial void MessagesBoostsUpdate(ref RequestDelegate? handler) => handler = ActionNotFound("update", "Messages::BoostsController");
}
