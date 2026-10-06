using Campfire.Web.Controllers;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// SessionsController (`resource :session`), FirstRunsController (`resource :first_run`) and
// WelcomeController (`root "welcome#show"`). The resource routes Rails draws for actions these
// controllers don't have raise AbstractController::ActionNotFound, a 404.
public static partial class Routes
{
    static partial void SessionsNew(ref RequestDelegate? handler) => handler = SessionsController.New;

    static partial void SessionsCreate(ref RequestDelegate? handler) => handler = SessionsController.Create;

    static partial void SessionsDestroy(ref RequestDelegate? handler) => handler = SessionsController.Destroy;

    static partial void SessionsShow(ref RequestDelegate? handler) => handler = ActionNotFound("show", "SessionsController");

    static partial void SessionsEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "SessionsController");

    static partial void SessionsUpdate(ref RequestDelegate? handler) => handler = ActionNotFound("update", "SessionsController");

    static partial void FirstRunsShow(ref RequestDelegate? handler) => handler = FirstRunsController.Show;

    static partial void FirstRunsCreate(ref RequestDelegate? handler) => handler = FirstRunsController.Create;

    static partial void FirstRunsNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "FirstRunsController");

    static partial void FirstRunsEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "FirstRunsController");

    static partial void FirstRunsUpdate(ref RequestDelegate? handler) => handler = ActionNotFound("update", "FirstRunsController");

    static partial void FirstRunsDestroy(ref RequestDelegate? handler) => handler = ActionNotFound("destroy", "FirstRunsController");

    static partial void WelcomeShow(ref RequestDelegate? handler) => handler = WelcomeController.Show;

    static RequestDelegate ActionNotFound(string action, string controller) =>
        _ => Task.FromException(new RoutingErrorException($"The action '{action}' could not be found for {controller}"));
}
