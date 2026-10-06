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
/// Rendering a page in the application layout for the sign-in, first-run and welcome controllers,
/// which may have no <c>Current.user</c> (signed out) and no <c>Current.account</c> (first run).
/// </summary>
static class SessionsPages
{
    /// <summary>
    /// The implicit render of an HTML-only action: a request that takes no HTML gets 406, as
    /// <c>ActionController::MissingExactTemplate</c> does.
    /// </summary>
    public static async ValueTask RenderActionAsync(this ApplicationController controller, Action<View, SqliteSession, HtmlWriter> template)
    {
        controller.RespondTo(MimeType.Html);
        await controller.RenderTemplateAsync(template).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>render :template, status:</c>: an HTML page whatever the request accepts; a request that
    /// takes no HTML has no template to get (a 500, as <c>ActionView::MissingTemplate</c>).
    /// </summary>
    public static async ValueTask RenderTemplateAsync(this ApplicationController controller, Action<View, SqliteSession, HtmlWriter> template, int status = 200)
    {
        if (controller.Request.NegotiateMime([MimeType.Html]) is null)
        {
            throw new InvalidOperationException("Missing template");
        }
        var page = await controller.ReadAsync(session =>
        {
            var view = NewView(controller, session);
            var body = RenderString(w => template(view, session, w));
            var buffer = new ArrayBufferWriter<byte>();
            view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(body));
            return buffer.WrittenMemory;
        }).ConfigureAwait(false);
        controller.Render(page, MimeType.Html.Value, status);
    }

    /// <summary>
    /// <c>User.administrator.first</c>'s name and e-mail address, for <c>accounts/_help_contact</c>.
    /// </summary>
    public static HelpContact? AdministratorContact(SqliteSession session) =>
        session.Query(
            """SELECT "users"."name", "users"."email_address" FROM "users" WHERE "users"."role" = 1 ORDER BY "users"."id" ASC LIMIT 1""",
            reader => new HelpContact(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))).FirstOrDefault();

    // The view a page renders in: the request, the current user and account (either may be
    // missing), and the app's assets.
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

/// <summary>The administrator <c>accounts/_help_contact</c> offers to e-mail.</summary>
public sealed record HelpContact(string Name, string? EmailAddress);
