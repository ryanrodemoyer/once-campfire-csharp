using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Users::PushSubscriptionsController (`resources :push_subscriptions` under users) and
// Users::PushSubscriptions::TestNotificationsController (`resources :test_notifications, only: :create`).
// Rails draws new/edit/show/update for the resource; those actions don't exist.
public static partial class Routes
{
    static partial void UsersPushSubscriptionsIndex(ref RequestDelegate? handler) => handler = UsersPushSubscriptionsController.Index;

    static partial void UsersPushSubscriptionsCreate(ref RequestDelegate? handler) => handler = UsersPushSubscriptionsController.Create;

    static partial void UsersPushSubscriptionsDestroy(ref RequestDelegate? handler) => handler = UsersPushSubscriptionsController.Destroy;

    static partial void UsersPushSubscriptionsNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "Users::PushSubscriptionsController");

    static partial void UsersPushSubscriptionsEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "Users::PushSubscriptionsController");

    static partial void UsersPushSubscriptionsShow(ref RequestDelegate? handler) => handler = ActionNotFound("show", "Users::PushSubscriptionsController");

    static partial void UsersPushSubscriptionsUpdate(ref RequestDelegate? handler) => handler = ActionNotFound("update", "Users::PushSubscriptionsController");

    static partial void UsersPushSubscriptionsTestNotificationsCreate(ref RequestDelegate? handler) =>
        handler = UsersPushSubscriptionsTestNotificationsController.Create;
}
