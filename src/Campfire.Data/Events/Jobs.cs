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

// `Message::BroadcastUnreadRoomJob.perform_later(message)` (message/broadcasts.rb
// `broadcast_unread_room_later`): tells the room's members who still have it unread, or are in it
// now, that it has new activity.
public sealed record BroadcastUnreadRoomJob(long MessageId) : Job
{
    public override string ClassName => "Message::BroadcastUnreadRoomJob";

    public override IReadOnlyList<long> ArgumentIds => [MessageId];
}

// `Room::DestroyJob.perform_later(room, former_member_ids)` (room.rb `destroy_later`): resets the
// former members' connections, then destroys the room's messages one at a time, then the room.
// The second argument is a plain array of user ids, not records.
public sealed record RoomDestroyJob(long RoomId, IReadOnlyList<long> FormerMemberIds) : Job
{
    public override string ClassName => "Room::DestroyJob";

    public override IReadOnlyList<long> ArgumentIds => [RoomId];
}
