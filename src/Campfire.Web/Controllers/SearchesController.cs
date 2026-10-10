using System.Text;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Searching;
using Campfire.Data.Sqlite;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>SearchesController</c> (reference/app/controllers/searches_controller.rb):
/// message search, recent searches recording and clearing.
/// </summary>
public sealed class SearchesController : ApplicationController
{
    static readonly ControllerCallbacks<SearchesController> Chain = Callbacks.For<SearchesController>();

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'searches' for nil");

    /// <summary><c>GET /searches</c>: search results and recent searches.</summary>
    public static readonly RequestDelegate Index = Action(Chain, c => c.IndexAsync());

    /// <summary><c>POST /searches</c>: record query in recent searches and redirect to results.</summary>
    public static readonly RequestDelegate Create = Action(Chain, async c =>
    {
        var rawQuery = c.Params["q"] as string;
        var query = SearchQuery.Sanitize(rawQuery);
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query), "ActiveRecord::NotNullViolation");
        }
        await c.WriteAsync(tx => RecentSearches.Record(tx, c.User.Id, query, c.Now)).ConfigureAwait(false);
        c.RedirectTo(Routes.SearchesUrl(c.UrlBase, new() { { "q", query } }));
    });

    /// <summary><c>DELETE /searches/clear</c>: clear user's recent searches and redirect.</summary>
    public static readonly RequestDelegate Clear = Action(Chain, async c =>
    {
        await c.WriteAsync(tx => RecentSearches.Clear(tx, c.User.Id)).ConfigureAwait(false);
        c.RedirectTo(Routes.SearchesUrl(c.UrlBase));
    });

    async ValueTask IndexAsync()
    {
        RespondTo(MimeType.Html);
        var rawQuery = Params["q"] as string;
        var sanitizedQuery = SearchQuery.Sanitize(rawQuery);
        var query = SearchQuery.IsPresent(sanitizedQuery) ? sanitizedQuery : null;
        var returnToRoom = await LastRoomVisitedAsync().ConfigureAwait(false);

        var html = await ReadAsync(session =>
        {
            var user = User;
            var recentSearches = RecentSearches.Ordered(session, user.Id);
            var messages = query is not null
                ? MessageSearch.Reachable(session, user.Id, query)
                : [];
            var messageViews = LoadMessages(session, messages);
            var view = NewView(session);
            var page = new SearchPage(query, rawQuery, recentSearches, returnToRoom, messageViews);
            var body = RenderString(w => view.SearchesIndex(w, page));
            return RenderLayout(view, body, Request.IsTurboFrameRequest);
        }).ConfigureAwait(false);

        Render(html, MimeType.Html.Value);
    }

    List<MessageView> LoadMessages(SqliteSession session, List<Message> messages) =>
        MessageViewsFor(session).Load(session, messages);

    MessageViews MessageViewsFor(SqliteSession session) =>
        new(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session)));

    View NewView(SqliteSession session)
    {
        var account = Accounts.First(session);
        return new View
        {
            Assets = App.RequireAssets(),
            Origin = UrlBase,
            RequestPath = Request.Path,
            RequestUrl = RequestUrl.Url,
            Referrer = Referer,
            FormAuthenticityToken = (action, method) => FormAuthenticityToken(action, method),
            StreamKeys = App.Keys,
            Flash = Flash,
            CurrentUser = new CurrentUser(User.Id, User.Name, User.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            RichTextContext = RichTextContext(session),
            Storage = App.RequireStorage(),
        };
    }

    RenderContext RichTextContext(SqliteSession session) => new(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host);

    static ReadOnlyMemory<byte> RenderLayout(View view, string page, bool turboFrame)
    {
        using var buffer = new PooledBufferWriter();
        if (turboFrame)
        {
            view.TurboRailsFrameLayout(new HtmlWriter(buffer), new SafeString(page));
        }
        else
        {
            view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(page));
        }
        return buffer.WrittenMemory;
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        using var buffer = new PooledBufferWriter();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
