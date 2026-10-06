using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/room.rb and rooms/*.rb
public static class Rooms
{
    const string select = $"SELECT {Room.Columns} FROM \"rooms\"";

    // `user.rooms`: rooms through the user's memberships.
    const string selectForUser = $"{select} INNER JOIN \"memberships\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = @user_id";

    // `scope :ordered, -> { order("LOWER(name)") }`
    const string ordered = "ORDER BY LOWER(name)";

    public static Room? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"rooms\".\"id\" = @id LIMIT 1", Room.Read, ("@id", id));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"rooms\"");

    // `Room.opens` / `closeds` / `directs`
    public static List<Room> OfType(SqliteSession session, RoomType type) =>
        session.Query($"{select} WHERE \"rooms\".\"type\" = @type", Room.Read, ("@type", type.ClassName()));

    // `Rooms::Open.pluck(:id)`
    public static List<long> IdsOfType(SqliteSession session, RoomType type) =>
        Sql.Ids(session, "SELECT \"rooms\".\"id\" FROM \"rooms\" WHERE \"rooms\".\"type\" = @type", ("@type", type.ClassName()));

    public static long CountOfType(SqliteSession session, RoomType type) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"rooms\" WHERE \"rooms\".\"type\" = @type", ("@type", type.ClassName()));

    // `Room.original`: `order(:created_at).first`.
    public static Room? Original(SqliteSession session) =>
        Sql.One(session, $"{select} ORDER BY \"rooms\".\"created_at\" ASC LIMIT 1", Room.Read);

    // `user.rooms`
    public static List<Room> ForUser(SqliteSession session, long userId) =>
        session.Query(selectForUser, Room.Read, ("@user_id", userId));

    // `user.rooms.find_by(id:)`
    public static Room? FindForUser(SqliteSession session, long userId, long roomId) =>
        Sql.One(session, $"{selectForUser} AND \"rooms\".\"id\" = @id LIMIT 1", Room.Read, ("@user_id", userId), ("@id", roomId));

    // `user.rooms.original`
    public static Room? OriginalForUser(SqliteSession session, long userId) =>
        Sql.One(session, $"{selectForUser} ORDER BY \"rooms\".\"created_at\" ASC LIMIT 1", Room.Read, ("@user_id", userId));

    // `user.rooms.directs` / `.opens` / `.closeds`
    public static List<Room> ForUserOfType(SqliteSession session, long userId, RoomType type) =>
        session.Query($"{selectForUser} AND \"rooms\".\"type\" = @type", Room.Read, ("@user_id", userId), ("@type", type.ClassName()));

    // `user.rooms.directs.pluck(:id)`
    public static List<long> IdsForUserOfType(SqliteSession session, long userId, RoomType type) =>
        Sql.Ids(session, "SELECT \"rooms\".\"id\" FROM \"rooms\" INNER JOIN \"memberships\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = @user_id AND \"rooms\".\"type\" = @type",
            ("@user_id", userId), ("@type", type.ClassName()));

    // `user.rooms.without_directs`
    public static List<Room> ForUserWithoutDirects(SqliteSession session, long userId) =>
        session.Query($"{selectForUser} AND \"rooms\".\"type\" != @type", Room.Read, ("@user_id", userId), ("@type", RoomTypes.DirectClassName));

    // `user.rooms.without_directs.find_by(id:)`
    public static Room? FindForUserWithoutDirects(SqliteSession session, long userId, long roomId) =>
        Sql.One(session, $"{selectForUser} AND \"rooms\".\"type\" != @type AND \"rooms\".\"id\" = @id LIMIT 1", Room.Read,
            ("@user_id", userId), ("@type", RoomTypes.DirectClassName), ("@id", roomId));

    // `bot.rooms.without_directs.ordered` (accounts/bots/_bot.html.erb)
    public static List<Room> ForUserWithoutDirectsOrdered(SqliteSession session, long userId) =>
        session.Query($"{selectForUser} AND \"rooms\".\"type\" != @type {ordered}", Room.Read, ("@user_id", userId), ("@type", RoomTypes.DirectClassName));

    // `Rooms::Direct.find_for(users)` (rooms/direct.rb): the first direct room, in the order of
    // `all.joins(:users)`, whose members are exactly these users.
    public static Room? FindDirectFor(SqliteSession session, IReadOnlyCollection<long> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var wanted = userIds.ToHashSet();
        var members = new Dictionary<long, HashSet<long>>();
        foreach (var (roomId, userId) in session.Query(
            "SELECT \"memberships\".\"room_id\", \"users\".\"id\" FROM \"users\" INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" INNER JOIN \"rooms\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"rooms\".\"type\" = @type",
            reader => (reader.GetInt64(0), reader.GetInt64(1)),
            ("@type", RoomTypes.DirectClassName)))
        {
            if (!members.TryGetValue(roomId, out var set))
            {
                members[roomId] = set = [];
            }
            set.Add(userId);
        }
        var candidates = session.Query(
            $"{select} INNER JOIN \"memberships\" ON \"memberships\".\"room_id\" = \"rooms\".\"id\" INNER JOIN \"users\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"rooms\".\"type\" = @type",
            Room.Read,
            ("@type", RoomTypes.DirectClassName));
        return candidates.FirstOrDefault(room => members.TryGetValue(room.Id, out var set) && set.SetEquals(wanted));
    }

    // `Rooms::<Type>.create!(name:, creator:)`
    public static Room Create(SqliteSession session, RoomType type, string? name, long creatorId, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "rooms" ("created_at", "creator_id", "name", "type", "updated_at") VALUES (@now, @creator_id, @name, @type, @now) RETURNING "id"
            """, ("@now", Db.Time(now)), ("@creator_id", creatorId), ("@name", name), ("@type", type.ClassName()));
        return Find(session, id)!;
    }

    // `Room.create_for(attributes, users:)`: the room, and memberships for the users.
    public static Room CreateFor(SqliteSession session, RoomType type, string? name, long creatorId, IReadOnlyList<long> userIds, DateTimeOffset now)
    {
        var room = Create(session, type, name, creatorId, now);
        Memberships.GrantTo(session, room, userIds);
        return room;
    }

    // `room.update!(name:, type:)`, writing only what changed. A direct room keeps its type
    // (`direct_rooms_keep_their_type`): changing it throws, as the validation fails in Rails.
    public static Room Update(SqliteSession session, Room room, DateTimeOffset now, Change<string?>? name = null, RoomType? type = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        var changes = new List<(string, object?)>();
        if (name is { Value: var newName } && newName != room.Name)
        {
            changes.Add(("name", newName));
        }
        if (type is { } newType && newType != room.Type)
        {
            if (room.IsDirect)
            {
                throw new InvalidOperationException("Validation failed: Type can't be changed for a direct room");
            }
            changes.Add(("type", newType.ClassName()));
        }
        return Sql.UpdateChanged(session, "rooms", room.Id, changes, now) ? Find(session, room.Id)! : room;
    }

    // `touch`, from `belongs_to :room, touch: true` on messages.
    public static void Touch(SqliteSession session, long roomId, DateTimeOffset now) => Sql.Touch(session, "rooms", roomId, now);

    // The room's own row. `room.destroy` deletes its memberships and destroys its messages first.
    public static void Delete(SqliteSession session, long roomId) => Sql.Delete(session, "rooms", roomId);
}
