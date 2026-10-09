using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Rooms::InvolvementsController (`resource :involvement, only: %i[ show update ]` under rooms).
public static partial class Routes
{
    static partial void RoomsInvolvementsShow(ref RequestDelegate? handler) => handler = RoomsInvolvementsController.Show;

    static partial void RoomsInvolvementsUpdate(ref RequestDelegate? handler) => handler = RoomsInvolvementsController.Update;
}
