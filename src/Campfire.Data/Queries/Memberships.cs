using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/membership.rb and membership/connectable.rb
public static class Memberships
{
    const string select = $"SELECT {Membership.Columns} FROM \"memberships\"";

    // `scope :visible, -> { where.not(involvement: :invisible) }`. A NULL involvement isn't
    // visible either: `!=` with NULL is never true.
    const string visible = "\"memberships\".\"involvement\" != 'invisible'";

    // `scope :disconnected, -> { where(connected_at: [ nil, ...CONNECTION_TTL.ago ]) }`
    const string disconnected = "(\"memberships\".\"connected_at\" IS NULL OR \"memberships\".\"connected_at\" < @cutoff)";

    // Rows per `INSERT`: SQLite binds at most 32,766 parameters per statement.
    const int insertBatchSize = 1000;

    public static Membership? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"memberships\".\"id\" = @id LIMIT 1", Membership.Read, ("@id", id));

    // `user.memberships.find_by(room_id:)` (concerns/room_scoped.rb)
    public static Membership? FindFor(SqliteSession session, long userId, long roomId) =>
        Sql.One(session, $"{select} WHERE \"memberships\".\"user_id\" = @user_id AND \"memberships\".\"room_id\" = @room_id LIMIT 1", Membership.Read,
            ("@user_id", userId), ("@room_id", roomId));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"memberships\"");

    // `user.memberships`
    public static List<Membership> ForUser(SqliteSession session, long userId) =>
        session.Query($"{select} WHERE \"memberships\".\"user_id\" = @user_id", Membership.Read, ("@user_id", userId));

    // `room.memberships`
    public static List<Membership> ForRoom(SqliteSession session, long roomId) =>
        session.Query($"{select} WHERE \"memberships\".\"room_id\" = @room_id", Membership.Read, ("@room_id", roomId));

    // `user.memberships.unread.count`: the push badge.
    public static long CountUnreadForUser(SqliteSession session, long userId) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"memberships\" WHERE \"memberships\".\"user_id\" = @user_id AND \"memberships\".\"unread_at\" IS NOT NULL", ("@user_id", userId));

    // `user.memberships.without_direct_rooms.count`
    public static long CountForUserWithoutDirectRooms(SqliteSession session, long userId) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"memberships\" INNER JOIN \"rooms\" \"room\" ON \"room\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = @user_id AND \"room\".\"type\" != @type",
            ("@user_id", userId), ("@type", RoomTypes.DirectClassName));

    // `user.memberships.with_ordered_room` and `user.memberships.visible.with_ordered_room`
    // (users/profiles_controller.rb, users/sidebars_controller.rb): each membership with its room,
    // by `LOWER(rooms.name)`.
    public static List<(Membership Membership, Room Room)> ForUserWithOrderedRoom(SqliteSession session, long userId, bool visibleOnly)
    {
        var condition = visibleOnly ? $" AND {visible}" : "";
        return session.Query(
            $"SELECT {Membership.Columns}, {Room.Columns} FROM \"memberships\" INNER JOIN \"rooms\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = @user_id{condition} ORDER BY LOWER(rooms.name)",
            reader => (Membership.Read(reader), Room.ReadAt(reader, 9)),
            ("@user_id", userId));
    }

    // `Membership.where(room_id: room_ids).pluck(:user_id).uniq` (users/sidebars_controller.rb)
    public static List<long> UserIdsInRooms(SqliteSession session, IReadOnlyList<long> roomIds)
    {
        ArgumentNullException.ThrowIfNull(roomIds);
        if (roomIds.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("room_id", roomIds);
        return Sql.Ids(session, $"SELECT \"memberships\".\"user_id\" FROM \"memberships\" WHERE \"memberships\".\"room_id\" IN ({placeholders})", parameters)
            .Distinct()
            .ToList();
    }

    // `Membership.connected`
    public static List<Membership> Connected(SqliteSession session, DateTimeOffset now) =>
        session.Query($"{select} WHERE \"memberships\".\"connected_at\" >= @cutoff", Membership.Read, ("@cutoff", Db.Time(Membership.ConnectionCutoff(now))));

    // `Membership.disconnected`
    public static List<Membership> Disconnected(SqliteSession session, DateTimeOffset now) =>
        session.Query($"{select} WHERE {disconnected}", Membership.Read, ("@cutoff", Db.Time(Membership.ConnectionCutoff(now))));

    // `room.memberships.visible.disconnected.where.not(user: creator).update_all(unread_at:,
    // updated_at:)`, from `Room#receive` (room.rb): marks the room unread for members who aren't
    // watching it.
    public static int MarkUnread(SqliteSession session, long roomId, long creatorId, DateTimeOffset unreadAt, DateTimeOffset now) =>
        session.Execute(
            $"UPDATE \"memberships\" SET \"unread_at\" = @unread_at, \"updated_at\" = @now WHERE \"memberships\".\"room_id\" = @room_id AND {visible} AND {disconnected} AND \"memberships\".\"user_id\" != @creator_id",
            ("@unread_at", Db.Time(unreadAt)), ("@now", Db.Time(now)), ("@room_id", roomId), ("@cutoff", Db.Time(Membership.ConnectionCutoff(now))), ("@creator_id", creatorId));

    // `memberships.grant_to(users)`: `Membership.insert_all` with the room's default involvement,
    // skipping users who are already members. SQLite stamps the timestamps.
    public static void GrantTo(SqliteSession session, Room room, IReadOnlyList<long> userIds)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(userIds);
        foreach (var batch in userIds.Chunk(insertBatchSize))
        {
            var rows = batch.Select((_, i) => $"(@room_id, @user_id{i}, @involvement, {Db.SqliteNow}, {Db.SqliteNow})");
            var parameters = batch.Select((userId, i) => ($"@user_id{i}", (object?)userId))
                .Append(("@room_id", room.Id))
                .Append(("@involvement", room.DefaultInvolvement.Name()))
                .ToArray();
            InsertAll(session, $"INSERT INTO \"memberships\" (\"room_id\",\"user_id\",\"involvement\",\"created_at\",\"updated_at\") VALUES {string.Join(", ", rows)} ON CONFLICT  DO NOTHING RETURNING \"id\"", parameters);
        }
    }

    // `Membership.insert_all(room_ids.collect { { room_id:, user_id: } })`, from
    // `grant_membership_to_open_rooms` (user.rb): the involvement is the column's default.
    public static void GrantRoomsTo(SqliteSession session, long userId, IReadOnlyList<long> roomIds)
    {
        ArgumentNullException.ThrowIfNull(roomIds);
        foreach (var batch in roomIds.Chunk(insertBatchSize))
        {
            var rows = batch.Select((_, i) => $"(@room_id{i}, @user_id, {Db.SqliteNow}, {Db.SqliteNow})");
            var parameters = batch.Select((roomId, i) => ($"@room_id{i}", (object?)roomId))
                .Append(("@user_id", userId))
                .ToArray();
            InsertAll(session, $"INSERT INTO \"memberships\" (\"room_id\",\"user_id\",\"created_at\",\"updated_at\") VALUES {string.Join(", ", rows)} ON CONFLICT  DO NOTHING RETURNING \"id\"", parameters);
        }
    }

    // The memberships `memberships.revoke_from(users)` destroys (`destroy_by user: users`), in
    // the order it loads them. Each one's `after_destroy_commit` resets the user's connections.
    public static List<Membership> RevokeFrom(SqliteSession session, long roomId, IReadOnlyList<long> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("user_id", userIds);
        var revoked = session.Query($"{select} WHERE \"memberships\".\"room_id\" = @room_id AND \"memberships\".\"user_id\" IN ({placeholders})", Membership.Read,
            [("@room_id", roomId), .. parameters]);
        foreach (var membership in revoked)
        {
            Delete(session, membership.Id);
        }
        return revoked;
    }

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "memberships", id);

    // `user.memberships.delete_all`
    public static int DeleteForUser(SqliteSession session, long userId) =>
        session.Execute("DELETE FROM \"memberships\" WHERE \"memberships\".\"user_id\" = @user_id", ("@user_id", userId));

    // `room.memberships.delete_all`
    public static int DeleteForRoom(SqliteSession session, long roomId) =>
        session.Execute("DELETE FROM \"memberships\" WHERE \"memberships\".\"room_id\" = @room_id", ("@room_id", roomId));

    // `memberships.without_direct_rooms.delete_all`, from `User#deactivate`.
    public static int DeleteForUserWithoutDirectRooms(SqliteSession session, long userId) =>
        session.Execute(
            "DELETE FROM \"memberships\" WHERE \"memberships\".\"id\" IN (SELECT \"memberships\".\"id\" FROM \"memberships\" INNER JOIN \"rooms\" \"room\" ON \"room\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = @user_id AND \"room\".\"type\" != @type)",
            ("@user_id", userId), ("@type", RoomTypes.DirectClassName));

    // `read`: `update!(unread_at: nil)`.
    public static Membership Read(SqliteSession session, Membership membership, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return Update(session, membership, now, membership.UnreadAt is null ? [] : [("unread_at", null)]);
    }

    // `update!(involvement:)`
    public static Membership UpdateInvolvement(SqliteSession session, Membership membership, Involvement involvement, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return Update(session, membership, now, membership.Involvement == involvement ? [] : [("involvement", involvement.Name())]);
    }

    // `Membership.disconnect_all`: everyone connected is disconnected (on boot).
    public static int DisconnectAll(SqliteSession session, DateTimeOffset now) =>
        session.Execute(
            "UPDATE \"memberships\" SET \"connected_at\" = NULL, \"connections\" = 0, \"updated_at\" = @now WHERE \"memberships\".\"connected_at\" >= @cutoff",
            ("@now", Db.Time(now)), ("@cutoff", Db.Time(Membership.ConnectionCutoff(now))));

    // `present`: `Membership.connect(self, connected? ? connections + 1 : 1)`, which sets the
    // count and `connected_at` and clears `unread_at`, without touching `updated_at`.
    public static Membership Present(SqliteSession session, Membership membership, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var connections = membership.IsConnected(now) ? membership.Connections + 1 : 1;
        session.Execute(
            "UPDATE \"memberships\" SET \"connections\" = @connections, \"connected_at\" = @now, \"unread_at\" = NULL WHERE \"memberships\".\"id\" = @id",
            ("@connections", connections), ("@now", Db.Time(now)), ("@id", membership.Id));
        return Find(session, membership.Id)!;
    }

    // `connected`: `increment_connections`, then `touch :connected_at`.
    public static Membership Connected(SqliteSession session, Membership membership, DateTimeOffset now) =>
        TouchConnectedAt(session, IncrementConnections(session, membership, now), now);

    // `disconnected`: `decrement_connections`, then `update! connected_at: nil` once there are none.
    public static Membership Disconnected(SqliteSession session, Membership membership, DateTimeOffset now)
    {
        var decremented = DecrementConnections(session, membership, now);
        if (decremented.Connections >= 1)
        {
            return decremented;
        }
        return Update(session, decremented, now, decremented.ConnectedAt is null ? [] : [("connected_at", null)]);
    }

    // `refresh_connection`: `increment_connections unless connected?`, then `touch :connected_at`.
    public static Membership RefreshConnection(SqliteSession session, Membership membership, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var counted = membership.IsConnected(now) ? membership : IncrementConnections(session, membership, now);
        return TouchConnectedAt(session, counted, now);
    }

    // `connected? ? increment!(:connections, touch: true) : update!(connections: 1)`
    static Membership IncrementConnections(SqliteSession session, Membership membership, DateTimeOffset now) =>
        ChangeConnections(session, membership, now, by: 1, reset: 1);

    // `connected? ? decrement!(:connections, touch: true) : update!(connections: 0)`
    static Membership DecrementConnections(SqliteSession session, Membership membership, DateTimeOffset now) =>
        ChangeConnections(session, membership, now, by: -1, reset: 0);

    static Membership ChangeConnections(SqliteSession session, Membership membership, DateTimeOffset now, int by, int reset)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (!membership.IsConnected(now))
        {
            return Update(session, membership, now, membership.Connections == reset ? [] : [("connections", reset)]);
        }
        // `update_counters(id, connections: by, touch: true)`
        session.Execute(
            "UPDATE \"memberships\" SET \"connections\" = COALESCE(\"connections\", 0) + @by, \"updated_at\" = @now WHERE \"memberships\".\"id\" = @id",
            ("@by", by), ("@now", Db.Time(now)), ("@id", membership.Id));
        return Find(session, membership.Id)!;
    }

    // `touch :connected_at`
    static Membership TouchConnectedAt(SqliteSession session, Membership membership, DateTimeOffset now)
    {
        session.Execute(
            "UPDATE \"memberships\" SET \"connected_at\" = @now, \"updated_at\" = @now WHERE \"memberships\".\"id\" = @id",
            ("@now", Db.Time(now)), ("@id", membership.Id));
        return Find(session, membership.Id)!;
    }

    static Membership Update(SqliteSession session, Membership membership, DateTimeOffset now, IReadOnlyList<(string, object?)> changes) =>
        Sql.UpdateChanged(session, "memberships", membership.Id, changes, now) ? Find(session, membership.Id)! : membership;

    static void InsertAll(SqliteSession session, string sql, (string Name, object? Value)[] parameters)
    {
        using var reader = session.Command(sql, parameters).ExecuteReader();
        while (reader.Read())
        {
        }
    }
}
