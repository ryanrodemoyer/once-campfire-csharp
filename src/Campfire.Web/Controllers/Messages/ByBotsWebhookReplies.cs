using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Webhooks;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Attachments;
using Campfire.RichText.Editing;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;

namespace Campfire.Web.Controllers;

/// <summary>
/// A bot's webhook reply (reference/app/models/webhook.rb, run by Bot::WebhookJob): a message from
/// the bot in the room, then its <c>broadcast_create</c>, rendered as a job renders it, by
/// <c>ApplicationController.renderer</c>: no request, so URLs point at <c>http://example.org</c>,
/// and no session or <c>Current.user</c>. The reply doesn't go to other bots' webhooks: only
/// MessagesController does that.
/// </summary>
public sealed partial class ByBotsWebhookReplies(WebApp app) : IWebhookReplies
{
    /// <summary>The renderer's default host.</summary>
    public static readonly UrlBase RendererOrigin = new("http", "example.org");

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// <c>room.messages.create!(body: text, creator: user)</c>: the text assigned to the rich text
    /// body as a posted body is.
    /// </summary>
    public async Task ReceiveTextAsync(Room room, User bot, WebhookTextReply reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(reply);
        // A reply that isn't valid UTF-8 fails the assignment in Rails (Encoding::CompatibilityError).
        var body = EditableContent.StoredBody(StrictUtf8.GetString(reply.Bytes));
        var (seams, now) = (app.RequireSeams(), app.Clock.GetUtcNow());
        var message = await app.Database.WriteAsync(transaction =>
        {
            var plainTextBody = RichTextPlainText.PlainTextBody(body, null, RichTextContext(transaction.Session, now));
            return MessageLifecycle.Create(transaction, seams, room.Id, bot.Id, null, body, plainTextBody, now);
        }, cancellationToken).ConfigureAwait(false);
        await BroadcastCreateAsync(room, message, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>ActiveStorage::Blob.create_and_upload!</c> of the reply, then
    /// <c>room.messages.create_with_attachment!(attachment:, creator: user)</c>, which M10 implements
    /// in its own file (Controllers/Messages/Attachments*), returning the message to broadcast.
    /// Without it, the job fails.
    /// </summary>
    public async Task ReceiveAttachmentAsync(Room room, User bot, WebhookAttachmentReply reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(reply);
        Func<CancellationToken, Task<Message>>? create = null;
        CreateAttachmentReply(room, bot, reply, ref create);
        var message = await (create ?? throw new NotSupportedException("Webhook attachment replies are M10's")).Invoke(cancellationToken).ConfigureAwait(false);
        await BroadcastCreateAsync(room, message, app.Clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    partial void CreateAttachmentReply(Room room, User bot, WebhookAttachmentReply reply, ref Func<CancellationToken, Task<Message>>? create);

    /// <summary>
    /// <c>message.broadcast_create</c> (message/broadcasts.rb): the message partial appended to the
    /// room's messages, then <c>broadcast_unread_room</c> to each member.
    /// </summary>
    async Task BroadcastCreateAsync(Room room, Message message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var broadcaster = app.RequireSeams().Broadcaster;
        var (html, memberIds) = await app.Database.ReadAsync(session =>
        {
            var view = NewView(session, now);
            var messageView = new MessageViews(app.Keys, body => RichTextPlainText.ToPlainText(body, RichTextContext(session, now))).Load(session, [message])[0];
            var buffer = new ArrayBufferWriter<byte>();
            view.MessagesCreate(new HtmlWriter(buffer), messageView, new RecordKey(room.Type.ClassName(), room.Id));
            var memberIds = session.Query(
                """SELECT "memberships"."user_id" FROM "memberships" WHERE "memberships"."room_id" = @room_id""",
                reader => reader.GetInt64(0), ("@room_id", room.Id));
            return (Encoding.UTF8.GetString(buffer.WrittenSpan), memberIds);
        }, cancellationToken).ConfigureAwait(false);

        broadcaster.Broadcast($"{RecordIdentifier.GidParam(room.Type.ClassName(), room.Id)}:messages", RailsJson.Encode(JsonValue.Create(html.TrimEnd('\n'))));
        var payload = RailsJson.Encode(new JsonObject { ["roomId"] = room.Id });
        foreach (var userId in memberIds)
        {
            broadcaster.Broadcast($"user_{userId}_unreads", payload);
        }
    }

    View NewView(SqliteSession session, DateTimeOffset now)
    {
        var account = Accounts.First(session);
        return new View
        {
            Assets = app.RequireAssets(),
            Origin = RendererOrigin,
            StreamKeys = app.Keys,
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = app.VapidPublicKey,
            AppVersion = app.AppVersion,
            RichTextContext = RichTextContext(session, now),
            Storage = app.RequireStorage(),
        };
    }

    // There's no request in a job, so no request host for attachment URLs.
    RenderContext RichTextContext(SqliteSession session, DateTimeOffset now) => new(new DatabaseAttachables(session, app.Keys, now), null);
}
