using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// `enum :involvement, %w[ invisible nothing mentions everything ].index_by(&:itself)`
// (reference/app/models/membership.rb): stored as the name.
public enum Involvement
{
    Invisible,
    Nothing,
    Mentions,
    Everything,
}

public static class Involvements
{
    public static string Name(this Involvement involvement) => involvement switch
    {
        Involvement.Invisible => "invisible",
        Involvement.Nothing => "nothing",
        Involvement.Mentions => "mentions",
        Involvement.Everything => "everything",
        _ => throw new ArgumentOutOfRangeException(nameof(involvement), involvement, null),
    };

    // The enum's value for a stored name, or null for NULL and for names it doesn't have, which
    // Rails reads as nil.
    public static Involvement? FromName(string? name) => name switch
    {
        "invisible" => Involvement.Invisible,
        "nothing" => Involvement.Nothing,
        "mentions" => Involvement.Mentions,
        "everything" => Involvement.Everything,
        _ => null,
    };
}

// A row of `memberships`: a user in a room, with their involvement, unread state and live
// connection count (reference/app/models/membership/connectable.rb).
public sealed record Membership(
    long Id,
    long RoomId,
    long UserId,
    Involvement? Involvement,
    DateTimeOffset? UnreadAt,
    DateTimeOffset? ConnectedAt,
    long Connections,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // `CONNECTION_TTL`
    public static readonly TimeSpan ConnectionTtl = TimeSpan.FromSeconds(60);

    internal const string Columns = """
        "memberships"."id", "memberships"."room_id", "memberships"."user_id", "memberships"."involvement", "memberships"."unread_at", "memberships"."connected_at", "memberships"."connections", "memberships"."created_at", "memberships"."updated_at"
        """;

    // `unread?`
    public bool IsUnread => UnreadAt is not null;

    // `connected?`: connected within the last CONNECTION_TTL.
    public bool IsConnected(DateTimeOffset now) => ConnectedAt is { } connectedAt && connectedAt >= ConnectionCutoff(now);

    // `CONNECTION_TTL.ago`
    public static DateTimeOffset ConnectionCutoff(DateTimeOffset now) => now - ConnectionTtl;

    internal static Membership Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Membership ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetInt64(at + 2),
        Involvements.FromName(Db.ReadNullableString(reader, at + 3)),
        Db.ReadNullableTime(reader, at + 4),
        Db.ReadNullableTime(reader, at + 5),
        reader.GetInt64(at + 6),
        Db.ReadTime(reader, at + 7),
        Db.ReadTime(reader, at + 8));
}
