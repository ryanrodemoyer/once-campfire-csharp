using System.Security.Cryptography;
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
/// <c>MessagesController#index</c> (reference/app/controllers/messages_controller.rb): one page of a
/// room's messages, the page before <c>before</c>, the page after <c>after</c>, or the last page.
/// <c>layout false</c>. <c>around</c> is rooms#show (M03). <c>room_not_found</c> is create's (M05).
/// </summary>
public sealed class MessagesIndexController : ApplicationController
{
    // RoomScoped's set_room, and the controller's own `before_action :set_room, except: :create`.
    // index is the only action here, so it always runs.
    static readonly ControllerCallbacks<MessagesIndexController> Chain = Callbacks.For<MessagesIndexController>()
        .Before("set_room", c => c.SetRoomAsync());

    Room? room;

    public static readonly RequestDelegate Index = Action(Chain, c => c.CacheReadResponseAsync(c.IndexAsync));

    Room CurrentRoom => room ?? throw new InvalidOperationException("set_room hasn't run");

    // `index`: page, then render and `fresh_when` its digest when there are any, else
    // `head :no_content`. The 204 happens before rendering, so an empty room as JSON is 204.
    async ValueTask IndexAsync()
    {
        var roomId = CurrentRoom.Id;
        var (before, after) = (PagingParam("before"), PagingParam("after"));
        var messages = await ReadAsync(session => FindPagedMessages(session, roomId, before, after)).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            Head(204);
            return;
        }

        // `render_to_string(:index)`: a request that takes no HTML has no template to get (a 500,
        // as ActionView::MissingTemplate).
        if (Request.NegotiateMime([MimeType.Html]) is null)
        {
            throw new InvalidOperationException("Missing template messages/index");
        }

        var page = await ReadAsync(session =>
        {
            var view = NewView(session);
            var loaded = LoadMessages(session, messages);
            return RenderString(w => view.MessagesIndex(w, loaded));
        }).ConfigureAwait(false);

        // Creator, body and boost edits can change HTML without touching messages:
        // `fresh_when etag: Digest::SHA256.hexdigest(body), template: false`, then the body as is.
        if (FreshWhen(new Freshness { Etag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(page))) }))
        {
            return;
        }
        SetResponseBody(page, MimeType.Html.Value);
    }

    async ValueTask SetRoomAsync() => room = (await RoomScoped.FindRoomAsync(this).ConfigureAwait(false)).Room;

    // `params[:before].present?` / `params[:after].present?`. before wins when both are present.
    string? PagingParam(string name) => Params[name] is { } value && RubyValues.IsPresent(value) ? RubyValues.ToS(value) : null;

    // `find_paged_messages`: `@room.messages.find`, so another room's message or a missing one is
    // a 404, and a non-numeric id is too (Active Record's integer cast misses, find raises).
    static List<Message> FindPagedMessages(SqliteSession session, long roomId, string? before, string? after)
    {
        if (before is not null)
        {
            return MessagePages.PageBefore(session, roomId, FindInRoom(session, roomId, before));
        }
        if (after is not null)
        {
            return MessagePages.PageAfter(session, roomId, FindInRoom(session, roomId, after));
        }
        return MessagePages.LastPage(session, roomId);
    }

    static Message FindInRoom(SqliteSession session, long roomId, string id) =>
        (ActiveModelInteger.Cast(id) is { } messageId ? Messages.FindInRoom(session, roomId, messageId) : null)
            ?? throw new RecordNotFoundException("Couldn't find Message");

    List<MessageView> LoadMessages(SqliteSession session, IReadOnlyList<Message> messages) =>
        new MessageViews(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session))).Load(session, messages);

    // The view a page renders in: the request, the current user and account, and the app's assets.
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
            RichTextContext = RichTextContext(session),
            Storage = App.RequireStorage(),
            Fragments = FragmentContext(),
        };
    }

    RenderContext RichTextContext(SqliteSession session) => new(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host);

    static string RenderString(Action<HtmlWriter> template)
    {
        using var buffer = new PooledBufferWriter();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
