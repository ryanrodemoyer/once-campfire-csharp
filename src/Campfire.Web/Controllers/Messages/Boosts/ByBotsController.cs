using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
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
/// <c>Messages::Boosts::ByBotsController</c>
/// (reference/app/controllers/messages/boosts/by_bots_controller.rb): a bot boosts a message in one
/// of its rooms with the raw request body (<c>RawRequestBody</c>) as the content, or removes its
/// own boost; each is broadcast to the room as Messages::BoostsController broadcasts it.
/// </summary>
public sealed class MessagesBoostsByBotsController : ApplicationController
{
    // Messages::BoostsController's callbacks, with set_message and set_boost overridden in place.
    static readonly ControllerCallbacks<MessagesBoostsByBotsController> Chain = Callbacks.For<MessagesBoostsByBotsController>()
        .Before("set_message", c => c.SetMessageAsync())
        .Before("set_boost", c => c.SetBoostAsync(), only: ["destroy"])
        .AllowBotAccess(only: ["create", "destroy"])
        .Before("ensure_content_present", c => c.EnsureContentPresent(), only: ["create"]);

    Room? room;
    Message? message;
    Boost? boost;

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    Room CurrentRoom => room ?? throw new InvalidOperationException("set_message hasn't run");

    Message CurrentMessage => message ?? throw new InvalidOperationException("set_message hasn't run");

    Boost CurrentBoost => boost ?? throw new InvalidOperationException("set_boost hasn't run");

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");

    async ValueTask CreateAsync()
    {
        // `@message.boosts.create!(boost_params)`
        var content = RawRequestBody();
        var (current, user, now) = (CurrentMessage, User, Now);
        boost = await WriteAsync(transaction =>
            BoostLifecycle.Create(transaction, current, user.Id, content, PlainTextBody(transaction.Session, current.Id), now)).ConfigureAwait(false);

        // `broadcast_create`: `@boost.broadcast_append_to @boost.message.room, :messages,
        //   target: "boosts_message_#{@boost.message.client_message_id}", partial: "messages/boosts/boost",
        //   attributes: { maintain_scroll: true }`
        var (html, json) = await ReadAsync(session =>
        {
            var view = NewView(session);
            var created = LoadMessage(session).Boosts.Single(boostView => boostView.Id == CurrentBoost.Id);
            return (RenderString(w => view.MessagesBoostsBoost(w, created)), view.MessagesBoostsByBotsShow(created));
        }).ConfigureAwait(false);
        var append = Tag.Element("turbo-stream", Tag.Template(new SafeString(html)), new HtmlOptions
        {
            { "maintain_scroll", true },
            { "action", "append" },
            { "target", $"boosts_message_{current.ClientMessageId}" },
        });
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(CurrentRoom), TurboStreamPayload(append.Value));

        // `render :show, status: :created`: only a JSON template.
        if (Request.Format != MimeType.Json)
        {
            throw new InvalidOperationException("Missing template messages/boosts/by_bots/show");
        }
        Render(json, MimeType.Json.Value, 201);
    }

    async ValueTask DestroyAsync()
    {
        var (destroyed, current, now) = (CurrentBoost, CurrentMessage, Now);
        // `@boost.destroy!`
        await WriteAsync(transaction => BoostLifecycle.Destroy(transaction, destroyed, current, PlainTextBody(transaction.Session, current.Id), now)).ConfigureAwait(false);

        // `@boost.broadcast_remove_to @boost.message.room, :messages`
        var remove = TurboStreamTags.Remove(RecordIdentifier.DomId(new RecordKey("Boost", destroyed.Id)));
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(CurrentRoom), TurboStreamPayload(remove.Value));

        // There is no destroy template: `head :no_content`.
        DefaultRender();
    }

    // `@message = Current.user.rooms.find_by(id: params[:room_id])&.messages&.find_by(id: params[:message_id])`,
    // else `head :not_found`.
    async ValueTask SetMessageAsync()
    {
        var userId = User.Id;
        var roomId = Params["room_id"] is string roomParam ? ActiveModelInteger.Cast(roomParam) : null;
        var messageId = Params["message_id"] is string messageParam ? ActiveModelInteger.Cast(messageParam) : null;
        (room, message) = await ReadAsync(session =>
        {
            var found = roomId is { } id ? Rooms.FindForUser(session, userId, id) : null;
            return (found, found is not null && messageId is { } inRoom ? Messages.FindInRoom(session, found.Id, inRoom) : null);
        }).ConfigureAwait(false);
        if (message is null)
        {
            Head(404);
        }
    }

    // `@boost = @message.boosts.find_by!(id: params[:id], booster: Current.user)`, with the
    // RecordNotFound rescued as `head :not_found`.
    async ValueTask SetBoostAsync()
    {
        var (messageId, userId) = (CurrentMessage.Id, User.Id);
        var id = Params["id"] is string param ? ActiveModelInteger.Cast(param) : null;
        boost = id is { } boostId ? await ReadAsync(session => Boosts.FindForBooster(session, messageId, boostId, userId)).ConfigureAwait(false) : null;
        if (boost is null)
        {
            Head(404);
        }
    }

    // `head :unprocessable_content if raw_request_body.blank?`
    void EnsureContentPresent()
    {
        if (RubyValues.IsBlank(RawRequestBody()))
        {
            Head(422);
        }
    }

    // RawRequestBody's `request.body.read.force_encoding("UTF-8")`. Invalid UTF-8 fails `blank?`
    // in Rails, a 500.
    string RawRequestBody() => StrictUtf8.GetString(Request.Body.Raw);

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    MessageView LoadMessage(SqliteSession session) =>
        new MessageViews(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session))).Load(session, [CurrentMessage])[0];

    // The view the JSON and the broadcast render in. A bot's request has no session, so the boost's
    // delete form carries no CSRF token, as the renderer's doesn't.
    View NewView(SqliteSession session)
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
            StreamKeys = App.Keys,
            CurrentUser = new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            RichTextContext = RichTextContext(session),
            Storage = App.RequireStorage(),
        };
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
