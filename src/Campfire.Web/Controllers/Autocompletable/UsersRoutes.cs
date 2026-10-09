using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Autocompletable::UsersController (`namespace :autocompletable` resources :users, only: :index).
public static partial class Routes
{
    static partial void AutocompletableUsersIndex(ref RequestDelegate? handler) => handler = AutocompletableUsersController.Index;
}
