using System.Buffers;
using System.Text;
using Campfire.Data.Pagination;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Ruby;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Rooms::RefreshesController</c> (reference/app/controllers/rooms/refreshes_controller.rb):
/// messages created or updated since <c>since</c>, as a turbo stream the open room appends and
/// replaces. There is no <c>fresh_when</c>. An empty refresh still renders the template.
/// </summary>
public sealed class RoomsRefreshesController : ApplicationController
{
    // RoomScoped's set_room, then the controller's set_last_updated_at.
    static readonly ControllerCallbacks<RoomsRefreshesController> Chain = Callbacks.For<RoomsRefreshesController>()
        .Before("set_room", c => c.SetRoomAsync())
        .Before("set_last_updated_at", c => c.SetLastUpdatedAt());

    Room? room;
    DateTimeOffset lastUpdatedAt;

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    Room CurrentRoom => room ?? throw new InvalidOperationException("set_room hasn't run");

    // `show`. The only template is show.turbo_stream, so HTML is 406 (any_templates? is true).
    async ValueTask ShowAsync()
    {
        if (Request.NegotiateMime([MimeType.TurboStream]) is null)
        {
            throw new UnknownFormatException();
        }

        var (current, since) = (CurrentRoom, lastUpdatedAt);
        // ActiveStorage::SetCurrent's URL options are the request's, through View.Origin.
        var page = await ReadAsync(session =>
        {
            var created = MessagePages.PageCreatedSince(session, current.Id, since);
            var updated = MessagePages.PageUpdatedSince(session, current.Id, since, [.. created.Select(message => message.Id)]);
            var view = NewView(session);
            var loader = new MessageViews(App.Keys, body => RichTextPlainText.ToPlainText(body, new RenderContext(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host)));
            return RenderString(w => view.RoomsRefreshesShow(w, current, loader.Load(session, created), loader.Load(session, updated)));
        }).ConfigureAwait(false);
        Render(page, MimeType.TurboStream.Value);
    }

    async ValueTask SetRoomAsync() => room = (await RoomScoped.FindRoomAsync(this).ConfigureAwait(false)).Room;

    // `Time.at(0, params[:since].to_i, :millisecond)`. A missing param is nil, and nil.to_i is 0.
    void SetLastUpdatedAt()
    {
        var millis = Params["since"] is string text ? RubyString.ToI(text) : 0;
        lastUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(millis);
    }

    View NewView(SqliteSession session)
    {
        var account = Accounts.First(session);
        var user = Current.User ?? throw new InvalidOperationException("undefined method 'memberships' for nil");
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
            CurrentUser = new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            RichTextContext = new RenderContext(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host),
            Storage = App.RequireStorage(),
        };
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
