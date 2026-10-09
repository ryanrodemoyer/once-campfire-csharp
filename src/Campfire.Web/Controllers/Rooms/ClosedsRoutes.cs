using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Rooms::ClosedsController (`resources :closeds` in the rooms namespace). index is
// RoomsController's, inherited, though GET /rooms/closeds reaches rooms#show first; destroy is
// too, without its room.
public static partial class Routes
{
    static partial void RoomsClosedsIndex(ref RequestDelegate? handler) => handler = RoomsController.Index;

    static partial void RoomsClosedsShow(ref RequestDelegate? handler) => handler = RoomsClosedsController.Show;

    static partial void RoomsClosedsNew(ref RequestDelegate? handler) => handler = RoomsClosedsController.New;

    static partial void RoomsClosedsCreate(ref RequestDelegate? handler) => handler = RoomsClosedsController.Create;

    static partial void RoomsClosedsEdit(ref RequestDelegate? handler) => handler = RoomsClosedsController.Edit;

    static partial void RoomsClosedsUpdate(ref RequestDelegate? handler) => handler = RoomsClosedsController.Update;

    static partial void RoomsClosedsDestroy(ref RequestDelegate? handler) => handler = RoomsClosedsController.Destroy;
}
