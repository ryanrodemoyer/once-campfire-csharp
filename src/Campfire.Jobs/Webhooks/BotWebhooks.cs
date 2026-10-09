using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Attachments;
using Campfire.RichText.PlainText;
using Campfire.Storage.Blobs;

namespace Campfire.Jobs.Webhooks;

/// <summary>
/// Where a bot's reply goes: <c>receive_text_reply_to</c> and <c>receive_attachment_reply_to</c>
/// (reference/app/models/webhook.rb), each a message from the bot in the room, then its
/// <c>broadcast_create</c>. The web app implements it, as the broadcast is a rendered partial.
/// </summary>
public interface IWebhookReplies
{
    /// <summary><c>room.messages.create!(body: text, creator: user).broadcast_create</c></summary>
    Task ReceiveTextAsync(Room room, User bot, WebhookTextReply reply, CancellationToken cancellationToken);

    /// <summary>
    /// <c>room.messages.create_with_attachment!(attachment: blob, creator: user).broadcast_create</c>,
    /// with the blob <c>ActiveStorage::Blob.create_and_upload!</c> makes of the reply.
    /// </summary>
    Task ReceiveAttachmentAsync(Room room, User bot, WebhookAttachmentReply reply, CancellationToken cancellationToken);
}

/// <summary>
/// <c>Bot::WebhookJob</c> (reference/app/jobs/bot/webhook_job.rb): <c>bot.deliver_webhook(message)</c>
/// (user/bot.rb), i.e. <c>webhook.deliver(message)</c>, which posts <see cref="Payload"/> to the
/// bot's webhook and turns the answer into the bot's reply.
/// </summary>
/// <param name="database">The app's database.</param>
/// <param name="client">The webhook's HTTP client.</param>
/// <param name="replies">Where replies go.</param>
/// <param name="attachables">
/// Action Text's record lookups for a session (the web app's <c>DatabaseAttachables</c>), which
/// <c>plain_text_body</c> resolves the body's attachments with.
/// </param>
public sealed class BotWebhooks(SqliteDatabase database, WebhookClient client, IWebhookReplies replies, Func<SqliteSession, IAttachableResolver> attachables)
{
    public void RegisterWith(JobRunner runner, JobQueueLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Register<WebhookJob>(PerformAsync, limits);
    }

    /// <summary>
    /// <c>perform(bot, message)</c>. A bot or message that's gone raises, as Active Job's
    /// <c>DeserializationError</c> does, and so does a bot without a webhook (<c>nil.deliver</c>).
    /// </summary>
    public async Task PerformAsync(WebhookJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var (bot, room, url, payload) = await database.ReadAsync(session =>
        {
            var bot = Users.Find(session, job.BotId) ?? throw Missing("User", job.BotId);
            var message = Messages.Find(session, job.MessageId) ?? throw Missing("Message", job.MessageId);
            var webhook = Data.Queries.Webhooks.ForUser(session, bot.Id) ?? throw new InvalidOperationException($"undefined method 'deliver' for nil (User {bot.Id} has no webhook)");
            var room = Rooms.Find(session, message.RoomId) ?? throw new InvalidOperationException($"undefined method 'id' for nil (Message {message.Id} has no room)");
            return (bot, room, webhook.Url, Payload(session, bot, room, message));
        }, cancellationToken).ConfigureAwait(false);

        var delivery = await client.DeliverAsync(url, payload, cancellationToken).ConfigureAwait(false);
        switch (delivery.Reply)
        {
            case WebhookTextReply text:
                await replies.ReceiveTextAsync(room, bot, text, cancellationToken).ConfigureAwait(false);
                break;
            case WebhookAttachmentReply attachment:
                await replies.ReceiveAttachmentAsync(room, bot, attachment, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// <c>payload(message)</c>: the message's creator, its room with the bot's API path for it,
    /// and the message: its stored HTML (<c>body.body</c>, null without a body) and its plain text
    /// without the bot's own mentions.
    /// </summary>
    public string Payload(SqliteSession session, User bot, Room room, Message message)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(message);
        var creator = Users.Find(session, message.CreatorId) ?? throw new InvalidOperationException($"undefined method 'id' for nil (Message {message.Id} has no creator)");
        var body = RichTexts.For(session, Message.ModelName, message.Id, "body")?.Body;
        var attachment = BlobRecords.FindAttachedBlob(session, Message.ModelName, message.Id, "attachment");
        var context = new RenderContext(attachables(session), RequestHost: null);
        var plainTextBody = RichTextPlainText.PlainTextBody(body, attachment?.Filename.ToString(), context);

        return RailsJson.Encode(new JsonObject
        {
            ["user"] = new JsonObject { ["id"] = creator.Id, ["name"] = creator.Name },
            ["room"] = new JsonObject { ["id"] = room.Id, ["name"] = room.Name, ["path"] = $"/rooms/{room.Id}/{bot.BotKey}/messages" },
            ["message"] = new JsonObject
            {
                ["id"] = message.Id,
                // `ActionText::Content#as_json`: its HTML, as the fragment serializes.
                ["body"] = new JsonObject { ["html"] = body is null ? null : RichTextRenderer.Load(body).ToHtml(), ["plain"] = RichTextPlainText.WithoutRecipientMentions(plainTextBody, bot.Name) },
                ["path"] = $"/rooms/{room.Id}/@{message.Id}",
            },
        });
    }

    static InvalidOperationException Missing(string model, long id) =>
        new($"ActiveJob::DeserializationError: Couldn't find {model} with 'id'={id}");
}
