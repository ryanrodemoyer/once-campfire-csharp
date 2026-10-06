using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Formatting;
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
/// The writing half of <c>MessagesController</c> (reference/app/controllers/messages_controller.rb):
/// <c>create</c>, <c>show</c>, <c>edit</c>, <c>update</c> and <c>destroy</c>. <c>index</c> is M04's.
/// Domain events go out through <see cref="WebApp.Seams"/> in the order Rails sends them.
/// </summary>
public sealed class MessagesWriteController : ApplicationController
{
    static readonly ControllerCallbacks<MessagesWriteController> Chain = Callbacks.For<MessagesWriteController>()
        .Before("set_room", c => c.SetRoomAsync(), except: ["create"])
        .Before("set_message", c => c.SetMessageAsync(), only: ["show", "edit", "update", "destroy"])
        .Before("ensure_can_administer", c => c.EnsureCanAdministerMessage(), only: ["edit", "update", "destroy"]);

    static readonly PermitFilter[] MessageParams = [PermitFilter.Key("body"), PermitFilter.Key("attachment"), PermitFilter.Key("client_message_id")];

    Room? room;
    Message? message;

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    public static readonly RequestDelegate Show = Action(Chain, c => c.RenderPageAsync((view, w, messageView) => view.MessagesShow(w, messageView)));

    public static readonly RequestDelegate Edit = Action(Chain, c => c.RenderPageAsync((view, w, messageView) => view.MessagesEdit(w, messageView)));

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    Room CurrentRoom => room ?? throw new InvalidOperationException("set_room hasn't run");

    Message CurrentMessage => message ?? throw new InvalidOperationException("set_message hasn't run");

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'messages' for nil");

    async ValueTask CreateAsync()
    {
        try
        {
            await SetRoomAsync().ConfigureAwait(false);
        }
        catch (RecordNotFoundException)
        {
            // `rescue ActiveRecord::RecordNotFoundException; render action: :room_not_found`
            await RenderRoomNotFoundAsync().ConfigureAwait(false);
            return;
        }

        var attributes = PermittedMessageParams();
        var body = attributes["body"] is { } posted ? StoredBody(posted) : null;
        var clientMessageId = attributes["client_message_id"] is { } id ? RubyValues.ToS(id) : null;
        var (room, user, now) = (CurrentRoom, User, Now);
        var seams = App.RequireSeams();

        message = await WriteAsync(transaction =>
        {
            var plainTextBody = RichTextPlainText.PlainTextBody(body, null, RichTextContext(transaction.Session));
            return MessageLifecycle.Create(transaction, seams, room.Id, user.Id, clientMessageId, body, plainTextBody, now);
        }).ConfigureAwait(false);

        // `@message.broadcast_create`, then `deliver_webhooks_to_bots`
        var html = await ReadAsync(session => RenderBroadcast(session, (view, w, messageView) => view.MessagesCreate(w, messageView, RoomRecord(room)))).ConfigureAwait(false);
        seams.Broadcaster.Broadcast(MessagesStream(room), TurboStreamPayload(html));
        await BroadcastUnreadRoomAsync(seams.Broadcaster).ConfigureAwait(false);
        var created = CurrentMessage;
        await ReadAsync(session =>
        {
            MessageLifecycle.DeliverWebhooksToBots(session, seams.Jobs, room, created, MentionedUserIds(session, body));
            return true;
        }).ConfigureAwait(false);

        // The implicit render: create has only a turbo_stream template.
        RespondTo(MimeType.TurboStream);
        Render(Encoding.UTF8.GetBytes(html), MimeType.TurboStream.Value);
    }

    async ValueTask UpdateAsync()
    {
        var attributes = PermittedMessageParams();
        var body = attributes["body"] is { } posted ? StoredBody(posted) : null;
        var clientMessageId = attributes["client_message_id"] is { } id ? RubyValues.ToS(id) : null;
        var (current, now) = (CurrentMessage, Now);

        // `@message.update!(message_params)`
        message = await WriteAsync(transaction =>
        {
            var session = transaction.Session;
            if (clientMessageId is not null && clientMessageId != current.ClientMessageId)
            {
                // The message's own save, its room's touch, and message/searchable.rb's
                // update_in_index after the commit.
                session.Execute("""UPDATE "messages" SET "client_message_id" = @client_message_id, "updated_at" = @now WHERE "messages"."id" = @id""",
                    ("@client_message_id", clientMessageId), ("@now", ActiveRecordTime.ToDb(now)), ("@id", current.Id));
                Rooms.Touch(session, current.RoomId, now);
                var plainTextBody = PlainTextBody(session, current.Id);
                transaction.AfterCommit(after => after.Execute("update message_search_index set body = @body where rowid = @rowid", ("@body", plainTextBody), ("@rowid", current.Id)));
            }
            if (body is not null)
            {
                MessageLifecycle.UpdateBody(transaction, current, body, RichTextPlainText.PlainTextBody(body, AttachmentFilename(session, current.Id), RichTextContext(session)), now);
            }
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
            // `format.json { render :show }`: there is no messages/show.json template.
            throw new InvalidOperationException("Missing template messages/show with formats: json");
        }
        RedirectTo(Routes.RoomMessageUrl(UrlBase, CurrentRoom.Id, CurrentMessage.Id));
    }

    async ValueTask DestroyAsync()
    {
        var (destroyed, now) = (CurrentMessage, Now);
        if (await ReadAsync(session => BlobRecords.FindAttachedBlob(session, Message.ModelName, destroyed.Id, "attachment") is not null).ConfigureAwait(false))
        {
            throw new NotImplementedRouteException("Destroying a message with an attachment is M10's (purge_later)");
        }
        await WriteAsync(transaction => MessageLifecycle.Destroy(transaction, destroyed, now)).ConfigureAwait(false);

        // `@message.broadcast_remove`
        var remove = TurboStreamTags.Remove(RecordIdentifier.DomId(new RecordKey(Message.ModelName, destroyed.ClientMessageId)));
        App.RequireSeams().Broadcaster.Broadcast(MessagesStream(CurrentRoom), TurboStreamPayload(remove.Value));

        // The implicit render: destroy has only a turbo_stream template.
        RespondTo(MimeType.TurboStream);
        Render(Encoding.UTF8.GetBytes(remove.Value + "\n"), MimeType.TurboStream.Value);
    }

    async ValueTask SetRoomAsync() => room = (await RoomScoped.FindRoomAsync(this).ConfigureAwait(false)).Room;

    // `@room.messages.find(params[:id])`
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

    // `params.require(:message).permit(:body, :attachment, :client_message_id)`. Attachments are
    // M10's (Active Storage uploads, analysis and thumbnails); until then a file is a 501.
    ParamHash PermittedMessageParams()
    {
        var attributes = Params.RequireHash("message").Permit(MessageParams);
        if (RubyValues.IsPresent(attributes["attachment"]))
        {
            throw new NotImplementedRouteException("Message attachments are M10's");
        }
        return attributes;
    }

    // `body = value` on has_rich_text: the value as `ActionText::Content` stores it.
    static string StoredBody(object posted) => EditableContent.StoredBody(RubyValues.ToS(posted));

    // `render action: :room_not_found`: an HTML page, whatever turbo_stream the request also
    // accepts; a request that takes no HTML has no template to get.
    async ValueTask RenderRoomNotFoundAsync()
    {
        if (Request.NegotiateMime([MimeType.Html]) is null)
        {
            throw new InvalidOperationException("Missing template messages/room_not_found");
        }
        var page = await ReadAsync(session =>
        {
            var view = NewView(session);
            return RenderLayout(view, RenderString(view.MessagesRoomNotFound));
        }).ConfigureAwait(false);
        Render(page, MimeType.Html.Value);
    }

    // The implicit render of an HTML-only action in the application layout.
    async ValueTask RenderPageAsync(Action<View, HtmlWriter, MessageView> template)
    {
        RespondTo(MimeType.Html);
        var page = await ReadAsync(session =>
        {
            var view = NewView(session);
            var messageView = LoadMessage(session);
            return RenderLayout(view, RenderString(w => template(view, w, messageView)));
        }).ConfigureAwait(false);
        Render(page, MimeType.Html.Value);
    }

    // A broadcast's render: `ApplicationController.renderer` has no session, so its forms carry no
    // authenticity tokens. create's response is the fragment the broadcast cached, so it has none
    // either.
    string RenderBroadcast(SqliteSession session, Action<View, HtmlWriter, MessageView> template)
    {
        var view = NewView(session, forgeryProtection: false);
        var messageView = LoadMessage(session);
        return RenderString(w => template(view, w, messageView));
    }

    MessageView LoadMessage(SqliteSession session) =>
        new MessageViews(App.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session))).Load(session, [CurrentMessage])[0];

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
    static ReadOnlyMemory<byte> RenderLayout(View view, string page)
    {
        var buffer = new ArrayBufferWriter<byte>();
        view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(page));
        return buffer.WrittenMemory;
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    RenderContext RichTextContext(SqliteSession session) => new(new DatabaseAttachables(session, App.Keys, Now), RequestUrl.Host);

    // `body.body.attachables.grep(User).uniq` (message/mentionee.rb), without Campfire's
    // invalid-signature fallback.
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

    // `plain_text_body` of a stored message.
    string PlainTextBody(SqliteSession session, long messageId) =>
        RichTextPlainText.PlainTextBody(RichTexts.For(session, Message.ModelName, messageId, "body")?.Body, AttachmentFilename(session, messageId), RichTextContext(session));

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
