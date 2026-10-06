using Campfire.Web.Controllers;

namespace Campfire.Web.Pipeline;

// allow_browser's block (reference/app/controllers/concerns/allow_browser.rb):
// `render template: "sessions/incompatible_browser"`, in the application layout, with the status
// the response already has.
public partial class ApplicationController
{
    static partial void IncompatibleBrowserPage(ref Func<ApplicationController, ValueTask>? render) =>
        render = controller => controller.RenderTemplateAsync(
            (view, _, w) => view.SessionsIncompatibleBrowser(w, controller.Platform.IsAppleMessages), controller.Status);
}
