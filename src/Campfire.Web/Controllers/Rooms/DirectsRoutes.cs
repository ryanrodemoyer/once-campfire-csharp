using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Rooms::DirectsController (`resources :directs` in the rooms namespace). index and show are
// RoomsController's, inherited; it has no update, so that route raises
// AbstractController::ActionNotFound, a 404.
public static partial class Routes
{
    static partial void RoomsDirectsIndex(ref RequestDelegate? handler) => handler = RoomsController.Index;

    static partial void RoomsDirectsShow(ref RequestDelegate? handler) => handler = RoomsDirectsController.Show;

    static partial void RoomsDirectsNew(ref RequestDelegate? handler) => handler = RoomsDirectsController.New;

    static partial void RoomsDirectsCreate(ref RequestDelegate? handler) => handler = RoomsDirectsController.Create;

    static partial void RoomsDirectsEdit(ref RequestDelegate? handler) => handler = RoomsDirectsController.Edit;

    static partial void RoomsDirectsUpdate(ref RequestDelegate? handler) => handler = ActionNotFound("update", "Rooms::DirectsController");

    static partial void RoomsDirectsDestroy(ref RequestDelegate? handler) => handler = RoomsDirectsController.Destroy;
}
