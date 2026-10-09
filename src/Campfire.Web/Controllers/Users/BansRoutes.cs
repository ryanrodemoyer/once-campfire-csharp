using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Users::BansController (`resource :ban, only: %i[ create destroy ]` under users).
public static partial class Routes
{
    static partial void UsersBansCreate(ref RequestDelegate? handler) => handler = UsersBansController.Create;

    static partial void UsersBansDestroy(ref RequestDelegate? handler) => handler = UsersBansController.Destroy;
}
