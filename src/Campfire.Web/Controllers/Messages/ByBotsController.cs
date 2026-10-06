using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Pagination;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.RichText.Attachments;
using Campfire.RichText.Editing;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Messages::ByBotsController</c> (reference/app/controllers/messages/by_bots_controller.rb), the
/// bot API under <c>/rooms/:room_id/:bot_key/messages</c> (JSON by route default): a room's
/// messages a page at a time, with <c>X-Total-Count</c> and a <c>Link</c> to the next page; and
/// MessagesController's create, update and destroy with the raw request body
/// (<c>RawRequestBody</c>) as the message's body, or a top-level multipart <c>attachment</c>.
/// </summary>
public sealed partial class MessagesByBotsController : ApplicationController
{
    // MessagesController's callbacks, each redeclared here and so moved, in this order, to the end
    // of the chain, as Rails does with a callback declared again.
    static readonly ControllerCallbacks<MessagesByBotsController> Chain = Callbacks.For<MessagesByBotsController>()
        .AllowBotAccess(only: ["index", "create", "update", "destroy"])
        .Before("set_room", c => c.SetRoomAsync())
        .Before("set_message", c => c.SetMessageAsync(), only: ["update", "destroy"])
        .Before("ensure_can_administer", c => c.EnsureCanAdministerMessage(), only: ["update", "destroy"])
        .Before("ensure_body_or_attachment_present", c => c.EnsureBodyOrAttachmentPresent(), only: ["create"]);

    static readonly PermitFilter[] AttachmentParams = [PermitFilter.Key("attachment")];

    Room? room;
    Message? message;

    public static readonly RequestDelegate Index = Action(Chain, c => c.IndexAsync());

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    Room CurrentRoom => room ?? throw new InvalidOperationException("set_room hasn't run");

    Message CurrentMessage => message ?? throw new InvalidOperationException("set_message hasn't run");

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");

    async ValueTask IndexAsync()
    {
        var current = CurrentRoom;
        var (before, after) = (PagingParam("before"), PagingParam("after"));
        var (messages, count, nextPage) = await ReadAsync(session =>
        {
            var messages = FindPagedMessages(session, current.Id, before, after);
            return (messages, Messages.CountInRoom(session, current.Id), NextPageParams(session, current.Id, messages, after is not null));
        }).ConfigureAwait(false);

        // `set_pagination_headers`
        Headers["X-Total-Count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (nextPage is { } page)
        {
            var url = Routes.RoomBotMessagesUrl(UrlBase, current.Id, Params["bot_key"], new RouteOptions { { page.Key, page.Id } });
            Headers["Link"] = $"<{url}>; rel=\"next\"";
        }

        // The implicit render: by_bots/index.json, or for HTML the index template the bot API
        // inherits from MessagesController, messages/index.html.erb (`layout false`), whose
        // forms carry tokens.
        if (RespondTo(MimeType.Json, MimeType.Html) == MimeType.Html)
        {
            var html = await ReadAsync(session =>
            {
                var view = NewView(session, forgeryProtection: true);
                return RenderString(w =>
                {
                    foreach (var messageView in LoadMessages(session, messages))
                    {
                        view.MessagesMessage(w, messageView);
                    }
                    w.WriteLiteral("\n"u8);
                });
            }).ConfigureAwait(false);
            Render(html, MimeType.Html.Value);
            return;
        }
        var json = await ReadAsync(session => NewView(session).MessagesByBotsIndex(LoadMessages(session, messages))).ConfigureAwait(false);
        Render(json, MimeType.Json.Value);
    }

    async ValueTask CreateAsync()
    {
        // MessagesController#create sets the room again, the same way.
        var (current, user, now) = (CurrentRoom, User, Now);
        var seams = App.RequireSeams();

        // `@room.messages.create_with_attachment!(message_params)`
        if (Params["attachment"] is not null)
        {
            Func<ValueTask<Message>>? create = null;
            CreateWithAttachment(Params.Permit(AttachmentParams), ref create);
            message = await (create ?? throw new NotImplementedRouteException("Message attachments are M10's")).Invoke().ConfigureAwait(false);
        }
        else
        {
            var body = EditableContent.StoredBody(RawRequestBody());
            message = await WriteAsync(transaction =>
            {
                var plainTextBody = RichTextPlainText.PlainTextBody(body, null, RichTextContext(transaction.Session));
                return MessageLifecycle.Create(transaction, seams, current.Id, user.Id, null, body, plainTextBody, now);
            }).ConfigureAwait(false);
        }

        // `@message.broadcast_create`, then `deliver_webhooks_to_bots`
        var created = CurrentMessage;
        var html = await ReadAsync(session => RenderBroadcast(session, (view, w, messageView) => view.MessagesCreate(w, messageView, RoomRecord(current)))).ConfigureAwait(false);
        seams.Broadcaster.Broadcast(MessagesStream(current), TurboStreamPayload(html));
        await BroadcastUnreadRoomAsync(seams.Broadcaster).ConfigureAwait(false);
        await ReadAsync(session =>
        {
            var body = RichTexts.For(session, Message.ModelName, created.Id, "body")?.Body;
            MessageLifecycle.DeliverWebhooksToBots(session, seams.Jobs, current, created, MentionedUserIds(session, body));
            return true;
        }).ConfigureAwait(false);

        // `head :created, location: message_url(@message)`
        Head(201, Routes.MessageUrl(UrlBase, created.Id));
    }

    async ValueTask UpdateAsync()
    {
        // `@message.update!(message_params)`
        if (Params["attachment"] is not null)
        {
            throw new NotImplementedRouteException("Replacing a message's attachment is M10's");
        }
        var body = EditableContent.StoredBody(RawRequestBody());
        var (current, now) = (CurrentMessage, Now);
        message = await WriteAsync(transaction =>
        {
            var session = transaction.Session;
            MessageLifecycle.UpdateBody(transaction, current, body, RichTextPlainText.PlainTextBody(body, AttachmentFilename(session, current.Id), RichTextContext(session)), now);
            return Messages.Find(session, current.Id)!;
        }).ConfigureAwait(false);

        // `@message.broadcast_replace_to @room, :messages, target: [ @message, :presentation ],
        //   partial: "messages/presentation", attributes: { maintain_scroll: true }`
        var presentation = await ReadAsync(session => RenderBroadcast(session, (view, w, messageView) => view.MessagesPresentation(w, messageView))).ConfigureAwait(false);
        var replace = Tag.Element("turbo-stream", Tag.Template(new SafeString(presentation)), new HtmlOptions
        {
            { "maintain_scroll", true },
            { "action", "replace" },
            { "target", RecordIdentifier.DomId(new RecordKey(Message.ModelName, CurrentMessage.ClientMessageId), "presentation") },
        });
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(CurrentRoom), TurboStreamPayload(replace.Value));

        if (RespondTo(MimeType.Html, MimeType.Json) == MimeType.Json)
        {
            // `format.json { render :show }`
            var json = await ReadAsync(session => NewView(session).MessagesByBotsShow(LoadMessages(session, [CurrentMessage])[0])).ConfigureAwait(false);
            Render(json, MimeType.Json.Value);
            return;
        }
        RedirectTo(Routes.RoomMessageUrl(UrlBase, CurrentRoom.Id, CurrentMessage.Id));
    }

    async ValueTask DestroyAsync()
    {
        var (destroyed, now) = (CurrentMessage, Now);
        // `@message.destroy`
        if (await ReadAsync(session => BlobRecords.FindAttachedBlob(session, Message.ModelName, destroyed.Id, "attachment") is not null).ConfigureAwait(false))
        {
            Func<ValueTask>? destroy = null;
            DestroyWithAttachment(destroyed, ref destroy);
            await (destroy ?? throw new NotImplementedRouteException("Destroying a message with an attachment is M10's")).Invoke().ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(transaction => MessageLifecycle.Destroy(transaction, destroyed, now)).ConfigureAwait(false);
        }

        // `@message.broadcast_remove`
        var remove = TurboStreamTags.Remove(RecordIdentifier.DomId(new RecordKey(Message.ModelName, destroyed.ClientMessageId)));
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(CurrentRoom), TurboStreamPayload(remove.Value));

        Head(204);
    }

    // `@room = Current.user.rooms.find_by(id: params[:room_id])`, else `head :not_found`.
    async ValueTask SetRoomAsync()
    {
        var userId = User.Id;
        var id = Params["room_id"] is string param ? ActiveModelInteger.Cast(param) : null;
        room = id is { } roomId ? await ReadAsync(session => Rooms.FindForUser(session, userId, roomId)).ConfigureAwait(false) : null;
        if (room is null)
        {
            Head(404);
        }
    }

    // MessagesController's `@room.messages.find(params[:id])`
    async ValueTask SetMessageAsync()
    {
        var roomId = CurrentRoom.Id;
        var id = Params["id"] is string param ? ActiveModelInteger.Cast(param) : null;
        message = (id is { } messageId ? await ReadAsync(session => Messages.FindInRoom(session, roomId, messageId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException("Couldn't find Message");
    }

    // `head :forbidden unless Current.user.can_administer?(@message)`
    void EnsureCanAdministerMessage()
    {
        if (!User.CanAdminister(CurrentMessage.CreatorId))
        {
            Head(403);
        }
    }

    // `head :unprocessable_content if params[:attachment].blank? && raw_request_body.blank?`
    void EnsureBodyOrAttachmentPresent()
    {
        if (RubyValues.IsBlank(Params["attachment"]) && RubyValues.IsBlank(RawRequestBody()))
        {
            Head(422);
        }
    }

    // RawRequestBody's `request.body.read.force_encoding("UTF-8")`: the body as sent, though for
    // multipart only its parsed parts are kept (see the status file's known gaps).
    string RawRequestBody() => Encoding.UTF8.GetString(Request.Body.Raw);

    // `params[:before].present?` / `params[:after].present?`: the id to page from.
    string? PagingParam(string name) => Params[name] is { } value && RubyValues.IsPresent(value) ? RubyValues.ToS(value) : null;

    // MessagesController's `find_paged_messages`: the page before or after the given message
    // (`@room.messages.find`, so another room's or a missing one is a 404), else the last page.
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

    // `next_page_params`: paging forward from the last message while newer ones exist, else back
    // from the first while older ones do.
    static (string Key, long Id)? NextPageParams(SqliteSession session, long roomId, List<Message> messages, bool after)
    {
        if (messages.Count == 0)
        {
            return null;
        }
        if (after)
        {
            return MessagePages.ExistsAfter(session, roomId, messages[^1]) ? ("after", messages[^1].Id) : null;
        }
        return MessagePages.ExistsBefore(session, roomId, messages[0]) ? ("before", messages[0].Id) : null;
    }

    // The attachment half of create and destroy, as in MessagesWriteController: M10 implements them
    // in its own file (Controllers/Messages/Attachments*). Without them, either answers 501.
    partial void CreateWithAttachment(ParamHash attributes, ref Func<ValueTask<Message>>? create);

    partial void DestroyWithAttachment(Message message, ref Func<ValueTask>? destroy);

    List<MessageView> LoadMessages(SqliteSession session, IReadOnlyList<Message> messages) =>
        new MessageViews(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session))).Load(session, messages);

    // A broadcast's render: `ApplicationController.renderer` has no session, so its forms carry no
    // authenticity tokens.
    string RenderBroadcast(SqliteSession session, Action<View, HtmlWriter, MessageView> template)
    {
        var view = NewView(session);
        var messageView = LoadMessages(session, [CurrentMessage])[0];
        return RenderString(w => template(view, w, messageView));
    }

    // The view the JSON, pages and broadcasts render in. A broadcast's render
    // (`ApplicationController.renderer`) has no session, so its forms carry no tokens.
    View NewView(SqliteSession session, bool forgeryProtection = false)
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

    // `body.body.attachables.grep(User).uniq` (message/mentionee.rb).
    List<long> MentionedUserIds(SqliteSession session, string? body)
    {
        if (body is null)
        {
            return [];
        }
        var context = RichTextContext(session);
        return [.. AttachmentResolution.AttachmentNodes(RichTextRenderer.Load(body))
            .Select(node => AttachmentResolution.ActionTextAttachableFromNode(node, context))
            .OfType<Mention>()
            .Select(mention => mention.User.Id)
            .Distinct()];
    }

    static string? AttachmentFilename(SqliteSession session, long messageId) =>
        BlobRecords.FindAttachedBlob(session, Message.ModelName, messageId, "attachment")?.Filename.ToString();

    // message/broadcasts.rb `broadcast_unread_room`: `{"roomId":…}` to each member, unencoded.
    async ValueTask BroadcastUnreadRoomAsync(IBroadcaster broadcaster)
    {
        var roomId = CurrentRoom.Id;
        var userIds = await ReadAsync(session => session.Query(
            """SELECT "memberships"."user_id" FROM "memberships" WHERE "memberships"."room_id" = @room_id""",
            reader => reader.GetInt64(0), ("@room_id", roomId))).ConfigureAwait(false);
        var payload = RailsJson.Encode(new JsonObject { ["roomId"] = roomId });
        foreach (var userId in userIds)
        {
            broadcaster.Broadcast($"user_{userId}_unreads", payload);
        }
    }

    static RecordKey RoomRecord(Room room) => new(room.Type.ClassName(), room.Id);

    // `[room, :messages]` as Turbo::StreamsChannel names it.
    static string MessagesStream(Room room) => $"{RecordIdentifier.GidParam(room.Type.ClassName(), room.Id)}:messages";

    // A turbo stream broadcast: the tag, without the template's trailing newline, JSON-encoded
    // as ActionCable.server.broadcast's default coder does.
    static string TurboStreamPayload(string html) => RailsJson.Encode(JsonValue.Create(html.TrimEnd('\n')));
}
