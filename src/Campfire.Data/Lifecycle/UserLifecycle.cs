using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Lifecycle;

// Users with their callbacks and the account changes that cut a user off
// (reference/app/models/user.rb, user/bannable.rb, user/bot.rb).
public static class UserLifecycle
{
    // `User.create!`: once it commits, `grant_membership_to_open_rooms`.
    public static User Create(
        WriteTransaction transaction,
        string name,
        string? emailAddress,
        string? passwordDigest,
        DateTimeOffset now,
        UserRole role = UserRole.Member,
        string? bio = null,
        string? botToken = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var user = Users.Create(transaction.Session, name, emailAddress, passwordDigest, now, role, bio, botToken);
        transaction.AfterCommit(after => Memberships.GrantRoomsTo(after, user.Id, Rooms.IdsOfType(after, RoomType.Open)));
        return user;
    }

    // `User.create_bot!(name:, webhook_url:)`: the bot is created and committed (granting it the
    // open rooms), then its webhook, if any, in a transaction of its own.
    public static async Task<User> CreateBotAsync(SqliteDatabase database, string name, string? webhookUrl, TimeProvider clock, string? bio = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        var bot = await database.WriteAsync(transaction =>
            Create(transaction, name, emailAddress: null, passwordDigest: null, clock.GetUtcNow(), UserRole.Bot, bio, User.GenerateBotToken())).ConfigureAwait(false);
        if (webhookUrl is not null)
        {
            await database.WriteAsync(transaction => Webhooks.Create(transaction.Session, bot.Id, webhookUrl, clock.GetUtcNow())).ConfigureAwait(false);
        }
        return bot;
    }

    // `deactivate`: the user's connections are closed first, then their memberships outside
    // direct rooms, push subscriptions, searches and sessions go, and the e-mail address is
    // freed (`deactived_email_address`, with `uuid` for `SecureRandom.uuid`).
    public static User Deactivate(WriteTransaction transaction, DomainSeams seams, User user, DateTimeOffset now, string? uuid = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(user);
        var session = transaction.Session;
        seams.Connections.Disconnect(user.Id, reconnect: false);
        Memberships.DeleteForUserWithoutDirectRooms(session, user.Id);
        PushSubscriptions.DeleteForUser(session, user.Id);
        Searches.DeleteForUser(session, user.Id);
        Sessions.DeleteForUser(session, user.Id);
        return Users.Update(session, user, now,
            status: UserStatus.Deactivated,
            emailAddress: new(User.DeactivatedEmailAddress(user.EmailAddress, uuid ?? SecureTokens.Uuid())));
    }

    // `ban`: a ban for each distinct public address the user has a session from, then
    // `apply_ban` (close their connections, end their sessions, enqueue the removal of their
    // messages) and the banned status. Active Job enqueues at once here, inside the
    // transaction: the reference doesn't defer jobs to the commit. A session from an address
    // that isn't public fails the ban's validation and rolls the whole ban back.
    public static User Ban(WriteTransaction transaction, DomainSeams seams, User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(user);
        var session = transaction.Session;
        foreach (var ipAddress in Sessions.IpAddressesForUser(session, user.Id))
        {
            if (BanAddresses.Error(ipAddress) is { } error)
            {
                throw new RecordInvalidException($"Validation failed: Ip address {error}");
            }
            Bans.Create(session, user.Id, ipAddress, now);
        }
        seams.Connections.Disconnect(user.Id, reconnect: false);
        Sessions.DeleteForUser(session, user.Id);
        seams.Jobs.Enqueue(new RemoveBannedContentJob(user.Id));
        return Users.Update(session, user, now, status: UserStatus.Banned);
    }

    // `unban`
    public static User Unban(WriteTransaction transaction, User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(user);
        Bans.DeleteForUser(transaction.Session, user.Id);
        return Users.Update(transaction.Session, user, now, status: UserStatus.Active);
    }
}
