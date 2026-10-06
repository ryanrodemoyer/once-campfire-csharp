using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Ruby;

namespace Campfire.Data.Queries;

// reference/app/models/user.rb and user/*.rb
public static class Users
{
    const string select = $"SELECT {User.Columns} FROM \"users\"";

    // `scope :ordered, -> { order("LOWER(name)") }`
    const string ordered = "ORDER BY LOWER(name)";

    const string active = "\"users\".\"status\" = 0";

    // `scope :active_bots, -> { active.where(role: :bot) }`
    const string activeBots = $"{active} AND \"users\".\"role\" = 2";

    // `scope :without_bots, -> { where.not(role: :bot) }`
    const string withoutBots = "\"users\".\"role\" != 2";

    public static User? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"users\".\"id\" = @id LIMIT 1", User.Read, ("@id", id));

    // `User.active.find_by(id:)`
    public static User? FindActive(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE {active} AND \"users\".\"id\" = @id LIMIT 1", User.Read, ("@id", id));

    // `User.where(id: ids)`, in the table's order.
    public static List<User> WhereIds(SqliteSession session, IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("id", ids);
        return session.Query($"{select} WHERE \"users\".\"id\" IN ({placeholders})", User.Read, parameters);
    }

    // The user `User.active.authenticate_by(email_address:, password:)` checks the password of.
    public static User? FindActiveByEmailAddress(SqliteSession session, string emailAddress) =>
        Sql.One(session, $"{select} WHERE {active} AND \"users\".\"email_address\" = @email_address LIMIT 1", User.Read, ("@email_address", emailAddress));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"users\"");

    // `User.active.ordered`
    public static List<User> ActiveOrdered(SqliteSession session) =>
        session.Query($"{select} WHERE {active} {ordered}", User.Read);

    // `User.active.pluck(:id)`
    public static List<long> ActiveIds(SqliteSession session) =>
        Sql.Ids(session, $"SELECT \"users\".\"id\" FROM \"users\" WHERE {active}");

    // `User.active.ordered.without_bots` (accounts/users_controller.rb)
    public static List<User> ActiveOrderedWithoutBots(SqliteSession session) =>
        session.Query($"{select} WHERE {active} AND {withoutBots} {ordered}", User.Read);

    // `User.where(status: statuses).ordered.without_bots` (accounts_controller.rb)
    public static List<User> WithStatusesOrderedWithoutBots(SqliteSession session, params IReadOnlyList<UserStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("status", statuses.Select(status => (int)status).ToList());
        return session.Query($"{select} WHERE \"users\".\"status\" IN ({placeholders}) AND {withoutBots} {ordered}", User.Read, parameters);
    }

    // `User.active_bots.ordered`
    public static List<User> ActiveBotsOrdered(SqliteSession session) =>
        session.Query($"{select} WHERE {activeBots} {ordered}", User.Read);

    // `User.active_bots.find(id)`
    public static User? FindActiveBot(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE {activeBots} AND \"users\".\"id\" = @id LIMIT 1", User.Read, ("@id", id));

    // `User.active.filtered_by(query).ordered`: `where("name like ?", "%#{query}%")`. The query
    // isn't escaped, so `%` and `_` in it are wildcards, as in Rails.
    public static List<User> ActiveFilteredByOrdered(SqliteSession session, string query) =>
        session.Query($"{select} WHERE {active} AND (name like @query) {ordered}", User.Read, ("@query", $"%{query}%"));

    // `User.authenticate_bot(bot_key)`: `bot_key.split("-")` into an id and a token, then
    // `active_bots.find_by(id:, bot_token:)`.
    public static User? AuthenticateBot(SqliteSession session, string botKey)
    {
        ArgumentNullException.ThrowIfNull(botKey);
        var parts = RubySplit(botKey, '-');
        var id = parts.Count > 0 ? ActiveModelInteger.Cast(parts[0]) : null;
        var token = parts.Count > 1 ? parts[1] : null;
        if (id is null)
        {
            return null;
        }
        var tokenCondition = token is null ? "\"users\".\"bot_token\" IS NULL" : "\"users\".\"bot_token\" = @token";
        return Sql.One(session, $"{select} WHERE {activeBots} AND \"users\".\"id\" = @id AND {tokenCondition} LIMIT 1", User.Read, ("@id", id), ("@token", token));
    }

    // `room.users`
    public static List<User> InRoom(SqliteSession session, long roomId) =>
        session.Query($"{select} INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"memberships\".\"room_id\" = @room_id", User.Read, ("@room_id", roomId));

    // `room.user_ids`
    public static List<long> IdsInRoom(SqliteSession session, long roomId) =>
        Sql.Ids(session, "SELECT \"users\".\"id\" FROM \"users\" INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"memberships\".\"room_id\" = @room_id", ("@room_id", roomId));

    // `room.users.active_bots`
    public static List<User> ActiveBotsInRoom(SqliteSession session, long roomId) =>
        session.Query($"{select} INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"memberships\".\"room_id\" = @room_id AND {activeBots}", User.Read, ("@room_id", roomId));

    // `room.users.where(id: ids)`: the message's `mentionees` (message/mentionee.rb).
    public static List<User> InRoomWhereIds(SqliteSession session, long roomId, IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("id", ids);
        return session.Query(
            $"{select} INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"memberships\".\"room_id\" = @room_id AND \"users\".\"id\" IN ({placeholders})",
            User.Read,
            [("@room_id", roomId), .. parameters]);
    }

    // The direct-room placeholders on the sidebar (users/sidebars_controller.rb):
    // `User.active.where.not(id: excluded).order(:created_at).limit(limit)`.
    public static List<User> ActiveExcludingByCreation(SqliteSession session, IReadOnlyList<long> excludedIds, int limit)
    {
        ArgumentNullException.ThrowIfNull(excludedIds);
        if (excludedIds.Count == 0)
        {
            return session.Query($"{select} WHERE {active} ORDER BY \"users\".\"created_at\" ASC LIMIT @limit", User.Read, ("@limit", limit));
        }
        var (placeholders, parameters) = Sql.List("id", excludedIds);
        return session.Query(
            $"{select} WHERE {active} AND \"users\".\"id\" NOT IN ({placeholders}) ORDER BY \"users\".\"created_at\" ASC LIMIT @limit",
            User.Read,
            [.. parameters, ("@limit", limit)]);
    }

    // `User.create!`: an active user. `role` defaults to member, as the column does.
    public static User Create(
        SqliteSession session,
        string name,
        string? emailAddress,
        string? passwordDigest,
        DateTimeOffset now,
        UserRole role = UserRole.Member,
        string? bio = null,
        string? botToken = null)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "users" ("bio", "bot_token", "created_at", "email_address", "name", "password_digest", "role", "status", "updated_at") VALUES (@bio, @bot_token, @now, @email_address, @name, @password_digest, @role, 0, @now) RETURNING "id"
            """,
            ("@bio", bio), ("@bot_token", botToken), ("@now", Db.Time(now)), ("@email_address", emailAddress),
            ("@name", name), ("@password_digest", passwordDigest), ("@role", (int)role));
        return Find(session, id)!;
    }

    // `User.create_bot!(name:)` without the webhook: a bot with a fresh token.
    public static User CreateBot(SqliteSession session, string name, DateTimeOffset now, string? bio = null) =>
        Create(session, name, emailAddress: null, passwordDigest: null, now, UserRole.Bot, bio, User.GenerateBotToken());

    // `update!` of the given attributes, writing only those that changed.
    public static User Update(
        SqliteSession session,
        User user,
        DateTimeOffset now,
        string? name = null,
        Change<string?>? emailAddress = null,
        Change<string?>? passwordDigest = null,
        UserRole? role = null,
        UserStatus? status = null,
        Change<string?>? bio = null,
        Change<string?>? botToken = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var changes = new List<(string, object?)>();
        if (bio is { Value: var newBio } && newBio != user.Bio)
        {
            changes.Add(("bio", newBio));
        }
        if (botToken is { Value: var newBotToken } && newBotToken != user.BotToken)
        {
            changes.Add(("bot_token", newBotToken));
        }
        if (emailAddress is { Value: var newEmailAddress } && newEmailAddress != user.EmailAddress)
        {
            changes.Add(("email_address", newEmailAddress));
        }
        if (name is not null && name != user.Name)
        {
            changes.Add(("name", name));
        }
        if (passwordDigest is { Value: var newPasswordDigest } && newPasswordDigest != user.PasswordDigest)
        {
            changes.Add(("password_digest", newPasswordDigest));
        }
        if (role is { } newRole && newRole != user.Role)
        {
            changes.Add(("role", (int)newRole));
        }
        if (status is { } newStatus && newStatus != user.Status)
        {
            changes.Add(("status", (int)newStatus));
        }
        return Sql.UpdateChanged(session, "users", user.Id, changes, now) ? Find(session, user.Id)! : user;
    }

    // `reset_bot_key`
    public static User ResetBotKey(SqliteSession session, User user, DateTimeOffset now) =>
        Update(session, user, now, botToken: new(User.GenerateBotToken()));

    // `String#split` without a limit: trailing empty fields are dropped.
    static List<string> RubySplit(string text, char separator)
    {
        var parts = text.Split(separator).ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }
        return parts;
    }
}
