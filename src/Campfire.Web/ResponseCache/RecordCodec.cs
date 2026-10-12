using System.Text;
using Campfire.Data.Records;

namespace Campfire.Web.ResponseCache;

/// <summary>
/// Hand-written snapshots of the rows <c>RecordCache</c> rebuilds
/// (reference/app/models/record_cache.rb). Timestamps keep their ticks, and enums keep the
/// integer or name they are stored as. Each decoded row is a new instance.
/// </summary>
static class RecordCodec
{
    public static byte[] EncodeRoom(Room room)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(room.Id);
            WriteString(writer, room.Name);
            writer.Write((int)room.Type);
            writer.Write(room.CreatorId);
            WriteTime(writer, room.CreatedAt);
            WriteTime(writer, room.UpdatedAt);
            writer.Write(room.MessagesCount);
        }
        return buffer.ToArray();
    }

    public static Room DecodeRoom(byte[] bytes)
    {
        using var reader = Reader(bytes);
        return new Room(
            reader.ReadInt64(),
            ReadString(reader),
            (RoomType)reader.ReadInt32(),
            reader.ReadInt64(),
            ReadTime(reader),
            ReadTime(reader),
            reader.ReadInt64());
    }

    public static byte[] EncodeMembership(Membership membership, Room room)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(membership.Id);
            writer.Write(membership.RoomId);
            writer.Write(membership.UserId);
            writer.Write(membership.Involvement is { } involvement ? (int)involvement : -1);
            WriteNullableTime(writer, membership.UnreadAt);
            WriteNullableTime(writer, membership.ConnectedAt);
            writer.Write(membership.Connections);
            WriteTime(writer, membership.CreatedAt);
            WriteTime(writer, membership.UpdatedAt);
            var encodedRoom = EncodeRoom(room);
            writer.Write(encodedRoom.Length);
            writer.Write(encodedRoom);
        }
        return buffer.ToArray();
    }

    public static (Membership Membership, Room Room) DecodeMembership(byte[] bytes)
    {
        using var reader = Reader(bytes);
        var membership = new Membership(
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt32() is var involvement && involvement < 0 ? null : (Involvement)involvement,
            ReadNullableTime(reader),
            ReadNullableTime(reader),
            reader.ReadInt64(),
            ReadTime(reader),
            ReadTime(reader));
        return (membership, DecodeRoom(reader.ReadBytes(reader.ReadInt32())));
    }

    public static byte[] EncodeSession(Session session, User user)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(session.Id);
            writer.Write(session.UserId);
            writer.Write(session.Token);
            WriteString(writer, session.IpAddress);
            WriteString(writer, session.UserAgent);
            WriteTime(writer, session.LastActiveAt);
            WriteTime(writer, session.CreatedAt);
            WriteTime(writer, session.UpdatedAt);
            var encodedUser = EncodeUser(user);
            writer.Write(encodedUser.Length);
            writer.Write(encodedUser);
        }
        return buffer.ToArray();
    }

    public static (Session Session, User User) DecodeSession(byte[] bytes)
    {
        using var reader = Reader(bytes);
        var session = new Session(
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadString(),
            ReadString(reader),
            ReadString(reader),
            ReadTime(reader),
            ReadTime(reader),
            ReadTime(reader));
        return (session, DecodeUser(reader.ReadBytes(reader.ReadInt32())));
    }

    static byte[] EncodeUser(User user)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(user.Id);
            writer.Write(user.Name);
            WriteString(writer, user.EmailAddress);
            WriteString(writer, user.PasswordDigest);
            writer.Write((int)user.Role);
            writer.Write((int)user.Status);
            WriteString(writer, user.Bio);
            WriteString(writer, user.BotToken);
            WriteTime(writer, user.CreatedAt);
            WriteTime(writer, user.UpdatedAt);
        }
        return buffer.ToArray();
    }

    static User DecodeUser(byte[] bytes)
    {
        using var reader = Reader(bytes);
        return new User(
            reader.ReadInt64(),
            reader.ReadString(),
            ReadString(reader),
            ReadString(reader),
            (UserRole)reader.ReadInt32(),
            (UserStatus)reader.ReadInt32(),
            ReadString(reader),
            ReadString(reader),
            ReadTime(reader),
            ReadTime(reader));
    }

    static BinaryReader Reader(byte[] bytes) => new(new MemoryStream(bytes), Encoding.UTF8);

    static void WriteString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    static string? ReadString(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    // DateTimeOffset(ticks, offset) treats ticks as the clock time, which is Ticks, not UtcTicks.
    static void WriteTime(BinaryWriter writer, DateTimeOffset time)
    {
        writer.Write(time.Ticks);
        writer.Write(time.Offset.Ticks);
    }

    static DateTimeOffset ReadTime(BinaryReader reader) =>
        new(reader.ReadInt64(), TimeSpan.FromTicks(reader.ReadInt64()));

    static void WriteNullableTime(BinaryWriter writer, DateTimeOffset? time)
    {
        writer.Write(time.HasValue);
        if (time is { } value)
        {
            WriteTime(writer, value);
        }
    }

    static DateTimeOffset? ReadNullableTime(BinaryReader reader) => reader.ReadBoolean() ? ReadTime(reader) : null;
}
