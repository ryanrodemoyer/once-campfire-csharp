using Campfire.Data.Queries;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>WelcomeController</c> (reference/app/controllers/welcome_controller.rb): the root, which sends
/// a user to the last room they visited, or says there are no rooms yet.
/// </summary>
public sealed class WelcomeController : ApplicationController
{
    static readonly ControllerCallbacks<WelcomeController> Chain = Callbacks.For<WelcomeController>();

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    async ValueTask ShowAsync()
    {
        var user = Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");
        if (await ReadAsync(session => Rooms.ForUser(session, user.Id).Count > 0).ConfigureAwait(false))
        {
            var room = await LastRoomVisitedAsync().ConfigureAwait(false) ?? throw new InvalidOperationException("No room to redirect to");
            RedirectTo(Routes.RoomUrl(RequestUrl.UrlBase, room.Id));
        }
        else
        {
            // An explicit `render`: a request that takes no HTML has no template (500, not 406).
            await this.RenderTemplateAsync((view, _, w) => view.WelcomeShow(w, user.Name)).ConfigureAwait(false);
        }
    }
}
