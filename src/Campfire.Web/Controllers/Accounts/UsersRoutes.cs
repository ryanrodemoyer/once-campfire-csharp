using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Accounts::UsersController (`resources :users` under `resource :account`). The resource routes
// it has no action for raise AbstractController::ActionNotFound, a 404.
public static partial class Routes
{
    static partial void AccountsUsersIndex(ref RequestDelegate? handler) => handler = AccountsUsersController.Index;

    static partial void AccountsUsersUpdate(ref RequestDelegate? handler) => handler = AccountsUsersController.Update;

    static partial void AccountsUsersDestroy(ref RequestDelegate? handler) => handler = AccountsUsersController.Destroy;

    static partial void AccountsUsersNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "Accounts::UsersController");

    static partial void AccountsUsersCreate(ref RequestDelegate? handler) => handler = ActionNotFound("create", "Accounts::UsersController");

    static partial void AccountsUsersShow(ref RequestDelegate? handler) => handler = ActionNotFound("show", "Accounts::UsersController");

    static partial void AccountsUsersEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "Accounts::UsersController");
}
