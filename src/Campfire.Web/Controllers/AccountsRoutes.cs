using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// AccountsController (`resource :account`), its join code, logo and custom styles, and
// UsersController (`join/:join_code` and `resources :users, only: :show`). The resource routes
// Rails draws for actions AccountsController doesn't have raise AbstractController::ActionNotFound.
public static partial class Routes
{
    static partial void AccountsEdit(ref RequestDelegate? handler) => handler = AccountsController.Edit;

    static partial void AccountsUpdate(ref RequestDelegate? handler) => handler = AccountsController.Update;

    static partial void AccountsNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "AccountsController");

    static partial void AccountsShow(ref RequestDelegate? handler) => handler = ActionNotFound("show", "AccountsController");

    static partial void AccountsCreate(ref RequestDelegate? handler) => handler = ActionNotFound("create", "AccountsController");

    static partial void AccountsDestroy(ref RequestDelegate? handler) => handler = ActionNotFound("destroy", "AccountsController");

    static partial void AccountsJoinCodesCreate(ref RequestDelegate? handler) => handler = AccountsJoinCodesController.Create;

    static partial void AccountsLogosShow(ref RequestDelegate? handler) => handler = AccountsLogosController.Show;

    static partial void AccountsLogosDestroy(ref RequestDelegate? handler) => handler = AccountsLogosController.Destroy;

    static partial void AccountsCustomStylesEdit(ref RequestDelegate? handler) => handler = AccountsCustomStylesController.Edit;

    static partial void AccountsCustomStylesUpdate(ref RequestDelegate? handler) => handler = AccountsCustomStylesController.Update;

    static partial void UsersNew(ref RequestDelegate? handler) => handler = UsersController.New;

    static partial void UsersCreate(ref RequestDelegate? handler) => handler = UsersController.Create;

    static partial void UsersShow(ref RequestDelegate? handler) => handler = UsersController.Show;
}
