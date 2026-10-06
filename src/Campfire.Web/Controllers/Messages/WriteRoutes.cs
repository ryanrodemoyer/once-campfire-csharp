using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// MessagesController's create, show, edit, update and destroy (`resources :messages` under
// rooms, and the unnested routes Rails also draws for them).
public static partial class Routes
{
    static partial void MessagesCreate(ref RequestDelegate? handler) => handler = MessagesWriteController.Create;

    static partial void MessagesShow(ref RequestDelegate? handler) => handler = MessagesWriteController.Show;

    static partial void MessagesEdit(ref RequestDelegate? handler) => handler = MessagesWriteController.Edit;

    static partial void MessagesUpdate(ref RequestDelegate? handler) => handler = MessagesWriteController.Update;

    static partial void MessagesDestroy(ref RequestDelegate? handler) => handler = MessagesWriteController.Destroy;
}
