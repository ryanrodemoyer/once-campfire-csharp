using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Rooms::OpensController (`resources :opens` in the rooms namespace). index is RoomsController's,
// inherited, though GET /rooms/opens reaches rooms#show first; destroy is too, without its room.
public static partial class Routes
{
    static partial void RoomsOpensIndex(ref RequestDelegate? handler) => handler = RoomsController.Index;

    static partial void RoomsOpensShow(ref RequestDelegate? handler) => handler = RoomsOpensController.Show;

    static partial void RoomsOpensNew(ref RequestDelegate? handler) => handler = RoomsOpensController.New;

    static partial void RoomsOpensCreate(ref RequestDelegate? handler) => handler = RoomsOpensController.Create;

    static partial void RoomsOpensEdit(ref RequestDelegate? handler) => handler = RoomsOpensController.Edit;

    static partial void RoomsOpensUpdate(ref RequestDelegate? handler) => handler = RoomsOpensController.Update;

    static partial void RoomsOpensDestroy(ref RequestDelegate? handler) => handler = RoomsOpensController.Destroy;
}
