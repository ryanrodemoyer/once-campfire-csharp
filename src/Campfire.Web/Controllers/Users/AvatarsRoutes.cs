using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Users::AvatarsController (`resource :avatar, only: %i[ show destroy ]` under `resources :users`).
public static partial class Routes
{
    static partial void UsersAvatarsShow(ref RequestDelegate? handler) => handler = UsersAvatarsController.Show;

    static partial void UsersAvatarsDestroy(ref RequestDelegate? handler) => handler = UsersAvatarsController.Destroy;
}
