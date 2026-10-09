using System.Buffers;
using System.Text;
using Campfire.Data.Queries;
using Campfire.Data.Sqlite;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>allow_browser</c>'s block renders <c>sessions/incompatible_browser</c> from a before callback,
/// before the action narrows <c>lookup_context.formats</c> to the request format. The HTML template
/// is found even for <c>.json</c> and <c>.js</c> (reference/app/controllers/concerns/allow_browser.rb).
/// <see cref="SessionsPages"/> refuses that, which is a 500 here; these controllers render the page.
/// </summary>
static class PwaIncompatibleBrowser
{
    public static async ValueTask RenderAsync(ApplicationController controller)
    {
        var (body, link) = await controller.ReadAsync(session =>
        {
            var view = NewView(controller, session);
            var page = RenderString(writer => view.SessionsIncompatibleBrowser(writer, controller.Platform.IsAppleMessages));
            var buffer = new ArrayBufferWriter<byte>();
            view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(page));
            return (buffer.WrittenMemory.ToArray(), view.LinkHeader);
        }).ConfigureAwait(false);
        if (link is not null)
        {
            controller.Headers["Link"] = link;
        }
        controller.Render(body, MimeType.Html.Value, controller.Status);
    }

    // The same view SessionsPages builds for a page in the application layout.
    static View NewView(ApplicationController controller, SqliteSession session)
    {
        var account = Accounts.First(session);
        var user = controller.Current.User;
        var app = controller.App;
        return new View
        {
            Assets = app.RequireAssets(),
            Origin = controller.RequestUrl.UrlBase,
            RequestPath = controller.Request.Path,
            RequestUrl = controller.RequestUrl.Url,
            Referrer = controller.Referer,
            FormAuthenticityToken = (action, method) => controller.FormAuthenticityToken(action, method),
            StreamKeys = app.Keys,
            Flash = controller.Flash,
            CurrentUser = user is null ? null : new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = app.VapidPublicKey,
            AppVersion = app.AppVersion,
            Storage = app.Storage,
        };
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
