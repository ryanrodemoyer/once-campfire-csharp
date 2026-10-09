using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Users::ProfilesController (`resource :profile` under `resources :users`). The resource routes it
// has no action for raise AbstractController::ActionNotFound, a 404.
public static partial class Routes
{
    static partial void UsersProfilesShow(ref RequestDelegate? handler) => handler = UsersProfilesController.Show;

    static partial void UsersProfilesUpdate(ref RequestDelegate? handler) => handler = UsersProfilesController.Update;

    static partial void UsersProfilesNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "Users::ProfilesController");

    static partial void UsersProfilesEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "Users::ProfilesController");

    static partial void UsersProfilesCreate(ref RequestDelegate? handler) => handler = ActionNotFound("create", "Users::ProfilesController");

    static partial void UsersProfilesDestroy(ref RequestDelegate? handler) => handler = ActionNotFound("destroy", "Users::ProfilesController");
}
