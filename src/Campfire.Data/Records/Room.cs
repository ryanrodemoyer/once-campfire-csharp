using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// The STI subclasses of Room (reference/app/models/rooms/*.rb).
public enum RoomType
{
    Open,
    Closed,
    Direct,
}

public static class RoomTypes
{
    // The `type` column's values.
    public const string OpenClassName = "Rooms::Open";
    public const string ClosedClassName = "Rooms::Closed";
    public const string DirectClassName = "Rooms::Direct";

    public static string ClassName(this RoomType type) => type switch
    {
        RoomType.Open => OpenClassName,
        RoomType.Closed => ClosedClassName,
        RoomType.Direct => DirectClassName,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    // Rails raises `SubclassNotFound` for a `type` it has no class for.
    public static RoomType FromClassName(string className) => className switch
    {
        OpenClassName => RoomType.Open,
        ClosedClassName => RoomType.Closed,
        DirectClassName => RoomType.Direct,
        _ => throw new InvalidDataException($"Invalid single-table inheritance type: {className} is not a subclass of Room"),
    };

    // `default_involvement`: "everything" in direct rooms (rooms/direct.rb), "mentions" elsewhere.
    public static Involvement DefaultInvolvement(this RoomType type) =>
        type == RoomType.Direct ? Involvement.Everything : Involvement.Mentions;
}

// A row of `rooms` (reference/app/models/room.rb). Direct rooms have no name.
public sealed record Room(
    long Id,
    string? Name,
    RoomType Type,
    long CreatorId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // The polymorphic name Rails gives rooms (`base_class`), e.g. in GlobalIDs.
    public const string ModelName = "Room";

    internal const string Columns = """
        "rooms"."id", "rooms"."name", "rooms"."type", "rooms"."creator_id", "rooms"."created_at", "rooms"."updated_at"
        """;

    public bool IsOpen => Type == RoomType.Open;

    public bool IsClosed => Type == RoomType.Closed;

    public bool IsDirect => Type == RoomType.Direct;

    public Involvement DefaultInvolvement => Type.DefaultInvolvement();

    internal static Room Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Room ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        Db.ReadNullableString(reader, at + 1),
        RoomTypes.FromClassName(reader.GetString(at + 2)),
        reader.GetInt64(at + 3),
        Db.ReadTime(reader, at + 4),
        Db.ReadTime(reader, at + 5));
}
