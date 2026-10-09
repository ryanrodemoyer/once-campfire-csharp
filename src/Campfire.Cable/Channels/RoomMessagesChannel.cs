using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Cable.Server;
using Campfire.Cable.Turbo;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;

namespace Campfire.Cable.Channels;

/// <summary>
/// <c>RoomMessagesChannel</c> (reference/app/channels/room_messages_channel.rb). Authorizes the
/// room message stream when the subscription is made, so revoking a membership stops delivery.
/// The room comes from the verified stream name, not from a parameter the client can point
/// somewhere else. <c>Turbo::StreamsChannel</c> turns these names away.
/// </summary>
public sealed class RoomMessagesChannel : Channel<User>
{
    /// <summary>
    /// Constants a GlobalID could name that aren't rooms. <c>only: Room</c> turns them away.
    /// Anything else is <c>constantize</c>'s <c>NameError</c>.
    /// </summary>
    static readonly HashSet<string> KnownModels = new(StringComparer.Ordinal)
    {
        "Account", "ActionText::RichText", "ActiveStorage::Attachment", "ActiveStorage::Blob", "ActiveStorage::VariantRecord",
        "ApplicationPlatform", "ApplicationRecord", "Ban", "Boost", "Current", "FirstRun", "Membership", "Message",
        "Opengraph::Document", "Opengraph::Fetch", "Opengraph::Location", "Opengraph::Metadata", "Purchaser",
        "Push::Subscription", "Search", "Session", "Sound", "User", "Webhook",
    };

    readonly SqliteDatabase database;
    readonly KeyGenerator keys;

    public RoomMessagesChannel(SqliteDatabase database, KeyGenerator keys)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(keys);
        this.database = database;
        this.keys = keys;
    }

    /// <summary><c>subscribed</c>: stream the authorized name, or reject.</summary>
    public override async ValueTask SubscribedAsync()
    {
        var streamName = await AuthorizedStreamNameAsync().ConfigureAwait(false);
        if (streamName is null)
        {
            Reject();
        }
        else
        {
            StreamFrom(streamName);
        }
    }

    /// <summary>
    /// Public methods: <c>subscribed</c>, and <c>verified_stream_name_from_params</c> from
    /// <c>Turbo::Streams::StreamName</c> (it returns without transmitting).
    /// </summary>
    public override async ValueTask<bool> PerformAsync(string action, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (SubscriptionRejected)
        {
            return false;
        }

        switch (action)
        {
            case "subscribed":
                await SubscribedAsync().ConfigureAwait(false);
                return true;
            case "verified_stream_name_from_params":
                _ = TurboStreams.VerifiedStreamName(keys, Params);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The verified stream name, when it's present and names a room the user belongs to.</summary>
    async ValueTask<string?> AuthorizedStreamNameAsync()
    {
        var streamName = TurboStreams.VerifiedStreamName(keys, Params);
        // `present?` is false for nil and blank.
        if (string.IsNullOrWhiteSpace(streamName))
        {
            return null;
        }

        var userId = CurrentUser.Id;
        var name = streamName;
        var room = await database.ReadAsync(session => SubscribableRoom(session, userId, name)).ConfigureAwait(false);
        return room is null ? null : streamName;
    }

    /// <summary><c>RoomMessagesChannel.subscribable_room</c>.</summary>
    static Room? SubscribableRoom(SqliteSession session, long userId, string streamName)
    {
        var parts = streamName.Split(':', 2);
        if (parts.Length != 2 || parts[1] != RoomStreams.Suffix)
        {
            return null;
        }

        var room = RoomFrom(session, parts[0]);
        return room is null ? null : Rooms.FindForUser(session, userId, room.Id);
    }

    /// <summary>
    /// <c>GlobalID::Locator.locate gid_param, only: Room</c>, with <c>RecordNotFound</c> as nil.
    /// An unknown constant raises <c>NameError</c> (<c>uninitialized constant</c>).
    /// </summary>
    static Room? RoomFrom(SqliteSession session, string gidParam)
    {
        var gid = GlobalId.Parse(gidParam) ?? GlobalId.FromParam(gidParam);
        if (gid is null)
        {
            return null;
        }

        RoomType? required;
        switch (gid.ModelName)
        {
            case "Room":
                required = null;
                break;
            case RoomTypes.OpenClassName:
                required = RoomType.Open;
                break;
            case RoomTypes.ClosedClassName:
                required = RoomType.Closed;
                break;
            case RoomTypes.DirectClassName:
                required = RoomType.Direct;
                break;
            default:
                if (KnownModels.Contains(gid.ModelName))
                {
                    return null;
                }

                throw new InvalidOperationException($"uninitialized constant {gid.ModelName}");
        }

        if (!long.TryParse(gid.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var room = Rooms.Find(session, id);
        if (room is null || (required is { } type && room.Type != type))
        {
            return null;
        }

        return room;
    }
}
