using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Sessions::TransfersController (`resources :transfers, only: %i[ show update ]` under `resource :session`).
public static partial class Routes
{
    static partial void SessionsTransfersShow(ref RequestDelegate? handler) => handler = SessionsTransfersController.Show;

    static partial void SessionsTransfersUpdate(ref RequestDelegate? handler) => handler = SessionsTransfersController.Update;
}
