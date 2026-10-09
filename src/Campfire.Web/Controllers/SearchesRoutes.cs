using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// SearchesController (`resources :searches, only: [:index, :create]` with `delete :clear`).
public static partial class Routes
{
    static partial void SearchesIndex(ref RequestDelegate? handler) => handler = SearchesController.Index;

    static partial void SearchesCreate(ref RequestDelegate? handler) => handler = SearchesController.Create;

    static partial void SearchesClear(ref RequestDelegate? handler) => handler = SearchesController.Clear;
}
