using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Accounts::BotsController and Accounts::Bots::KeysController (`resources :bots` with `resource :key`).
// The show route raises AbstractController::ActionNotFound, a 404, as Rails does.
public static partial class Routes
{
    static partial void AccountsBotsIndex(ref RequestDelegate? handler) => handler = AccountsBotsController.Index;

    static partial void AccountsBotsNew(ref RequestDelegate? handler) => handler = AccountsBotsController.New;

    static partial void AccountsBotsCreate(ref RequestDelegate? handler) => handler = AccountsBotsController.Create;

    static partial void AccountsBotsShow(ref RequestDelegate? handler) => handler = ActionNotFound("show", "Accounts::BotsController");

    static partial void AccountsBotsEdit(ref RequestDelegate? handler) => handler = AccountsBotsController.Edit;

    static partial void AccountsBotsUpdate(ref RequestDelegate? handler) => handler = AccountsBotsController.Update;

    static partial void AccountsBotsDestroy(ref RequestDelegate? handler) => handler = AccountsBotsController.Destroy;

    static partial void AccountsBotsKeysUpdate(ref RequestDelegate? handler) => handler = AccountsBotsKeysController.Update;
}
