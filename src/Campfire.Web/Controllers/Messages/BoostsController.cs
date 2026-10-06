using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
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
/// <c>Messages::BoostsController</c> (reference/app/controllers/messages/boosts_controller.rb):
/// a message's boosts, the boost form, and creating and deleting the user's own boosts, each
/// broadcast to the message's room.
/// </summary>
public sealed class MessagesBoostsController : ApplicationController
{
    static readonly ControllerCallbacks<MessagesBoostsController> Chain = Callbacks.For<MessagesBoostsController>()
        .Before("set_message", c => c.SetMessageAsync())
        .Before("set_boost", c => c.SetBoostAsync(), only: ["destroy"]);

    static readonly PermitFilter[] BoostParams = [PermitFilter.Key("content")];

    Message? message;
    Boost? boost;

    public static readonly RequestDelegate Index = Action(Chain, c => c.RenderPageAsync((view, w, messageView, _) => view.MessagesBoostsIndex(w, messageView)));

    public static readonly RequestDelegate New = Action(Chain, c => c.RenderPageAsync((view, w, messageView, user) => view.MessagesBoostsNew(w, messageView, user)));

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    Message CurrentMessage => message ?? throw new InvalidOperationException("set_message hasn't run");

    Boost CurrentBoost => boost ?? throw new InvalidOperationException("set_boost hasn't run");

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'reachable_messages' for nil");

    async ValueTask CreateAsync()
    {
        // `@message.boosts.create!(boost_params)`: content is NOT NULL, so a missing one fails.
        var content = Params.RequireHash("boost").Permit(BoostParams)["content"] is { } posted ? RubyValues.ToS(posted) : null;
        var (current, user, now) = (CurrentMessage, User, Now);
        boost = await WriteAsync(transaction =>
        {
            if (content is null)
            {
                throw new InvalidOperationException("NOT NULL constraint failed: boosts.content");
            }
            return BoostLifecycle.Create(transaction, current, user.Id, content, PlainTextBody(transaction.Session, current.Id), now);
        }).ConfigureAwait(false);

        BroadcastCreate(await ReadAsync(RenderBoostBroadcast).ConfigureAwait(false));
        RedirectTo(Routes.MessageBoostsUrl(UrlBase, current.Id));
    }

    async ValueTask DestroyAsync()
    {
        var (destroyed, current, now) = (CurrentBoost, CurrentMessage, Now);
        // `@boost.destroy!`
        await WriteAsync(transaction => BoostLifecycle.Destroy(transaction, destroyed, current, PlainTextBody(transaction.Session, current.Id), now)).ConfigureAwait(false);

        // `@boost.broadcast_remove_to @boost.message.room, :messages`
        var remove = TurboStreamTags.Remove(RecordIdentifier.DomId(new RecordKey("Boost", destroyed.Id)));
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(await FindRoomAsync().ConfigureAwait(false)), TurboStreamPayload(remove.Value));

        // There is no destroy template: `head :no_content`.
        DefaultRender();
    }

    // `@message = Current.user.reachable_messages.find(params[:message_id])`: a message in one of
    // the user's rooms (through any membership, and only while the room exists).
    async ValueTask SetMessageAsync()
    {
        var userId = User.Id;
        var id = Params["message_id"] is string param ? ActiveModelInteger.Cast(param) : null;
        message = (id is { } messageId ? await ReadAsync(session =>
            Messages.Find(session, messageId) is { } found && Memberships.FindFor(session, userId, found.RoomId) is not null && Rooms.Find(session, found.RoomId) is not null
                ? found
                : null).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException("Couldn't find Message");
    }

    // `@boost = @message.boosts.find_by!(id: params[:id], booster: Current.user)`
    async ValueTask SetBoostAsync()
    {
        var (messageId, userId) = (CurrentMessage.Id, User.Id);
        var id = Params["id"] is string param ? ActiveModelInteger.Cast(param) : null;
        boost = (id is { } boostId ? await ReadAsync(session => Boosts.FindForBooster(session, messageId, boostId, userId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException("Couldn't find Boost");
    }

    // `@boost.broadcast_append_to @boost.message.room, :messages,
    //   target: "boosts_message_#{@boost.message.client_message_id}", partial: "messages/boosts/boost",
    //   attributes: { maintain_scroll: true }`
    void BroadcastCreate((Room Room, string Html) rendered)
    {
        var append = Tag.Element("turbo-stream", Tag.Template(new SafeString(rendered.Html)), new HtmlOptions
        {
            { "maintain_scroll", true },
            { "action", "append" },
            { "target", $"boosts_message_{CurrentMessage.ClientMessageId}" },
        });
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(rendered.Room), TurboStreamPayload(append.Value));
    }

    // The boost partial as the broadcast renders it: `ApplicationController.renderer` has no
    // session, so its delete form carries no authenticity token.
    (Room, string) RenderBoostBroadcast(SqliteSession session)
    {
        var view = NewView(session, forgeryProtection: false);
        var created = LoadMessage(session).Boosts.Single(boostView => boostView.Id == CurrentBoost.Id);
        var room = Rooms.Find(session, CurrentMessage.RoomId) ?? throw new InvalidOperationException("The message's room is gone");
        return (room, RenderString(w => view.MessagesBoostsBoost(w, created)));
    }

    async ValueTask<Room> FindRoomAsync()
    {
        var roomId = CurrentMessage.RoomId;
        return await ReadAsync(session => Rooms.Find(session, roomId)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The message's room is gone");
    }

    // The implicit render of an HTML-only action, in turbo-rails' frame layout when a Turbo frame
    // asked for it, else the application layout.
    // The template gets the message and the current user as the partials read them.
    async ValueTask RenderPageAsync(Action<View, HtmlWriter, MessageView, MessageUser> template)
    {
        RespondTo(MimeType.Html);
        var page = await ReadAsync(session =>
        {
            var view = NewView(session);
            var views = MessageViewsFor(session);
            var messageView = views.Load(session, [CurrentMessage])[0];
            var user = views.User(User);
            return RenderLayout(view, RenderString(w => template(view, w, messageView, user)), Request.IsTurboFrameRequest);
        }).ConfigureAwait(false);
        Render(page, MimeType.Html.Value);
    }

    MessageViews MessageViewsFor(SqliteSession session) =>
        new(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session)));

    MessageView LoadMessage(SqliteSession session) => MessageViewsFor(session).Load(session, [CurrentMessage])[0];

    // The view a page renders in: the request, the current user and account, and the app's assets.
    View NewView(SqliteSession session, bool forgeryProtection = true)
    {
        var account = Accounts.First(session);
        var user = User;
        return new View
        {
            Assets = App.RequireAssets(),
            Origin = UrlBase,
            RequestPath = Request.Path,
            RequestUrl = RequestUrl.Url,
            Referrer = Referer,
            FormAuthenticityToken = forgeryProtection ? (action, method) => FormAuthenticityToken(action, method) : null,
            StreamKeys = App.Keys,
            Flash = Flash,
            CurrentUser = new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            RichTextContext = RichTextContext(session),
            Storage = App.RequireStorage(),
        };
    }

    // The page is rendered first, so its content_for calls are in place for the layout.
    static ReadOnlyMemory<byte> RenderLayout(View view, string page, bool turboFrame)
    {
        var buffer = new ArrayBufferWriter<byte>();
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
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    RenderContext RichTextContext(SqliteSession session) => new(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host);

    // `plain_text_body`, which the message's re-index after its touch stores.
    string PlainTextBody(SqliteSession session, long messageId) =>
        RichTextPlainText.PlainTextBody(RichTexts.For(session, Message.ModelName, messageId, "body")?.Body,
            BlobRecords.FindAttachedBlob(session, Message.ModelName, messageId, "attachment")?.Filename.ToString(), RichTextContext(session));

    // `[room, :messages]` as Turbo::StreamsChannel names it.
    static string MessagesStream(Room room) => $"{RecordIdentifier.GidParam(room.Type.ClassName(), room.Id)}:messages";

    // A turbo stream broadcast: the tag JSON-encoded, as ActionCable.server.broadcast's default
    // coder does.
    static string TurboStreamPayload(string html) => RailsJson.Encode(JsonValue.Create(html.TrimEnd('\n')));
}
