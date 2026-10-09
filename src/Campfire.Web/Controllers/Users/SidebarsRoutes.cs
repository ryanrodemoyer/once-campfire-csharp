using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Users::SidebarsController (`resource :sidebar, only: :show` under `resources :users`).
public static partial class Routes
{
    static partial void UsersSidebarsShow(ref RequestDelegate? handler) => handler = UsersSidebarsController.Show;
}
