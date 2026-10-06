namespace Campfire.Data.Events;

// The reference's Active Job classes (reference/app/jobs), with the records they take as ids:
// Active Job serializes records as GlobalIDs and loads them again when the job runs.
public abstract record Job
{
    // The Active Job class name, e.g. "Room::PushMessageJob".
    public abstract string ClassName { get; }

    // The arguments, as the ids of the records they name.
    public abstract IReadOnlyList<long> ArgumentIds { get; }
}

// `Room::PushMessageJob.perform_later(room, message)` (room.rb `push_later`): web pushes for a
// new message (room/message_pusher.rb).
public sealed record PushMessageJob(long RoomId, long MessageId) : Job
{
    public override string ClassName => "Room::PushMessageJob";

    public override IReadOnlyList<long> ArgumentIds => [RoomId, MessageId];
}

// `Bot::WebhookJob.perform_later(bot, message)` (user/bot.rb `deliver_webhook_later`): posts the
// message to the bot's webhook.
public sealed record WebhookJob(long BotId, long MessageId) : Job
{
    public override string ClassName => "Bot::WebhookJob";

    public override IReadOnlyList<long> ArgumentIds => [BotId, MessageId];
}

// `RemoveBannedContentJob.perform_later(user)` (user/bannable.rb): destroys a banned user's
// messages.
public sealed record RemoveBannedContentJob(long UserId) : Job
{
    public override string ClassName => "RemoveBannedContentJob";

    public override IReadOnlyList<long> ArgumentIds => [UserId];
}
