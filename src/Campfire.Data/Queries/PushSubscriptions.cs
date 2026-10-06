using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/push/subscription.rb
public static class PushSubscriptions
{
    const string select = $"SELECT {PushSubscription.Columns} FROM \"push_subscriptions\"";

    public static PushSubscription? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"push_subscriptions\".\"id\" = @id LIMIT 1", PushSubscription.Read, ("@id", id));

    // `user.push_subscriptions.find(id)`
    public static PushSubscription? FindForUser(SqliteSession session, long userId, long id) =>
        Sql.One(session, $"{select} WHERE \"push_subscriptions\".\"user_id\" = @user_id AND \"push_subscriptions\".\"id\" = @id LIMIT 1", PushSubscription.Read,
            ("@user_id", userId), ("@id", id));

    // `user.push_subscriptions.find_by(endpoint:, p256dh_key:, auth_key:)`
    // (users/push_subscriptions_controller.rb)
    public static PushSubscription? FindForUserByKeys(SqliteSession session, long userId, string? endpoint, string? p256dhKey, string? authKey) =>
        Sql.One(session,
            $"{select} WHERE \"push_subscriptions\".\"user_id\" = @user_id AND {Equals("endpoint", endpoint)} AND {Equals("p256dh_key", p256dhKey)} AND {Equals("auth_key", authKey)} LIMIT 1",
            PushSubscription.Read,
            ("@user_id", userId), ("@endpoint", endpoint), ("@p256dh_key", p256dhKey), ("@auth_key", authKey));

    // `user.push_subscriptions`
    public static List<PushSubscription> ForUser(SqliteSession session, long userId) =>
        session.Query($"{select} WHERE \"push_subscriptions\".\"user_id\" = @user_id", PushSubscription.Read, ("@user_id", userId));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"push_subscriptions\"");

    public static long CountForUser(SqliteSession session, long userId) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"push_subscriptions\" WHERE \"push_subscriptions\".\"user_id\" = @user_id", ("@user_id", userId));

    // Who `Room::MessagePusher` pushes a message to (room/message_pusher.rb): subscriptions of the
    // room's visible, disconnected members other than the creator, with the given involvement,
    // and among `userIds` when given (the mentionees).
    public static List<PushSubscription> ForRoomPush(
        SqliteSession session,
        long roomId,
        long creatorId,
        Involvement involvement,
        DateTimeOffset now,
        IReadOnlyList<long>? userIds = null)
    {
        var sql = $"""
            {select} INNER JOIN "users" ON "users"."id" = "push_subscriptions"."user_id" INNER JOIN "memberships" ON "memberships"."user_id" = "users"."id" WHERE "memberships"."involvement" != 'invisible' AND ("memberships"."connected_at" IS NULL OR "memberships"."connected_at" < @cutoff) AND "memberships"."room_id" = @room_id AND "memberships"."user_id" != @creator_id AND "memberships"."involvement" = @involvement
            """;
        (string Name, object? Value)[] parameters =
        [
            ("@cutoff", Db.Time(Membership.ConnectionCutoff(now))), ("@room_id", roomId), ("@creator_id", creatorId), ("@involvement", involvement.Name()),
        ];
        if (userIds is not null)
        {
            if (userIds.Count == 0)
            {
                return [];
            }
            var (placeholders, idParameters) = Sql.List("user_id", userIds);
            sql += $" AND \"push_subscriptions\".\"user_id\" IN ({placeholders})";
            parameters = [.. parameters, .. idParameters];
        }
        return session.Query(sql, PushSubscription.Read, parameters);
    }

    // The row of `user.push_subscriptions.create!(...)`. The caller validates the endpoint first.
    public static PushSubscription Create(SqliteSession session, long userId, string? endpoint, string? p256dhKey, string? authKey, string? userAgent, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "push_subscriptions" ("auth_key", "created_at", "endpoint", "p256dh_key", "updated_at", "user_agent", "user_id") VALUES (@auth_key, @now, @endpoint, @p256dh_key, @now, @user_agent, @user_id) RETURNING "id"
            """, ("@auth_key", authKey), ("@now", Db.Time(now)), ("@endpoint", endpoint), ("@p256dh_key", p256dhKey), ("@user_agent", userAgent), ("@user_id", userId));
        return Find(session, id)!;
    }

    // `subscription.touch` (a browser re-subscribing with the same keys)
    public static void Touch(SqliteSession session, long id, DateTimeOffset now) => Sql.Touch(session, "push_subscriptions", id, now);

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "push_subscriptions", id);

    // `user.push_subscriptions.delete_all`
    public static int DeleteForUser(SqliteSession session, long userId) =>
        session.Execute("DELETE FROM \"push_subscriptions\" WHERE \"push_subscriptions\".\"user_id\" = @user_id", ("@user_id", userId));

    // `where(column: value)`, which is `IS NULL` for nil.
    static string Equals(string column, string? value) =>
        value is null ? $"\"push_subscriptions\".\"{column}\" IS NULL" : $"\"push_subscriptions\".\"{column}\" = @{column}";
}
