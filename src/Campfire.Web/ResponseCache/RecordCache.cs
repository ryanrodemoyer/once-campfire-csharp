using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Web.Pipeline;

namespace Campfire.Web.ResponseCache;

/// <summary>
/// Request-local rows rebuilt from an immutable snapshot
/// (reference/app/models/record_cache.rb). The observer must still report the captured
/// version after the lookup and again before the snapshot is kept.
/// A miss or a bypass returns the loader's own instances.
/// </summary>
public static class RecordCache
{
    public static T[] Fetch<T>(
        FragmentStore store,
        ResponseCache cache,
        bool transactionOpen,
        string key,
        CacheVersion? version,
        Func<T[]> load,
        Func<T, bool> admissible,
        Func<T[], byte[]> encode,
        Func<byte[], T[]> decode)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(admissible);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(decode);
        // reference/app/models/record_cache.rb: an open transaction is never snapshotted.
        if (version is null || cache.Budget <= 0 || transactionOpen || FragmentStore.TransactionOpen)
        {
            return load();
        }
        var expanded = Expand(version, key);
        if (Encoding.UTF8.GetByteCount(expanded) > ResponseCache.MaxKeyBytes)
        {
            return load();
        }
        if (store.Read(expanded) is { } snapshot && cache.Version() == version)
        {
            return decode(snapshot);
        }
        var records = load();
        if (Array.TrueForAll(records, record => admissible(record)) && cache.Version() == version)
        {
            var encoded = encode(records);
            if (encoded.Length <= ResponseCache.MaxEntryBytes)
            {
                store.Write(expanded, encoded);
            }
        }
        return records;
    }

    /// <summary><c>["record-snapshot-v1", version, key]</c> expanded to one string.</summary>
    public static string Expand(CacheVersion version, string key) =>
        string.Create(CultureInfo.InvariantCulture, $"record-snapshot-v1\n{version.Database}\n{version.Namespace}\n{version.DataVersion}\n{key}");
}

/// <summary>The three <c>RecordCache.fetch</c> calls the controllers make.</summary>
public static class RecordSnapshots
{
    public static async ValueTask<Room?> RoomAsync(ApplicationController controller, string key, Func<SqliteSession, Room?> load)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(load);
        var version = controller.RecordCacheVersion();
        var cache = controller.App.Responses;
        var store = controller.App.Fragments;
        if (!Admits(cache, version, key, out var expanded))
        {
            return await controller.ReadAsync(load).ConfigureAwait(false);
        }
        if (store.Read(expanded) is { } snapshot && cache.Version() == version)
        {
            return RecordCodec.DecodeRoom(snapshot);
        }
        var room = await controller.ReadAsync(load).ConfigureAwait(false);
        if (room is not null && cache.Version() == version)
        {
            Keep(store, expanded, RecordCodec.EncodeRoom(room));
        }
        return room;
    }

    public static async ValueTask<(Membership Membership, Room Room)> MembershipAsync(
        ApplicationController controller,
        long userId,
        long roomId)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var version = controller.RecordCacheVersion();
        var cache = controller.App.Responses;
        var store = controller.App.Fragments;
        var key = string.Create(CultureInfo.InvariantCulture, $"membership/{userId}/{roomId}");
        if (!Admits(cache, version, key, out var expanded))
        {
            return await controller.ReadAsync(session => LoadMembership(session, userId, roomId)).ConfigureAwait(false);
        }
        if (store.Read(expanded) is { } snapshot && cache.Version() == version)
        {
            return RecordCodec.DecodeMembership(snapshot);
        }
        var found = await controller.ReadAsync(session => LoadMembership(session, userId, roomId)).ConfigureAwait(false);
        if (cache.Version() == version)
        {
            Keep(store, expanded, RecordCodec.EncodeMembership(found.Membership, found.Room));
        }
        return found;
    }

    public static async ValueTask<(Session? Session, User? User)> SessionAsync(Controller controller, string token)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(token);
        var version = (controller as ApplicationController)?.RecordCacheVersion();
        var cache = controller.App.Responses;
        var store = controller.App.Fragments;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var key = "session/" + digest;
        if (!Admits(cache, version, key, out var expanded))
        {
            return await controller.ReadAsync(session => LoadSession(session, token)).ConfigureAwait(false);
        }
        if (store.Read(expanded) is { } snapshot && cache.Version() == version)
        {
            return RecordCodec.DecodeSession(snapshot);
        }
        var found = await controller.ReadAsync(session => LoadSession(session, token)).ConfigureAwait(false);
        if (found.Session is not null && found.User is not null && cache.Version() == version)
        {
            Keep(store, expanded, RecordCodec.EncodeSession(found.Session, found.User));
        }
        return found;
    }

    static bool Admits(ResponseCache cache, CacheVersion? version, string key, out string expanded)
    {
        expanded = "";
        if (version is null || cache.Budget <= 0 || FragmentStore.TransactionOpen)
        {
            return false;
        }
        expanded = RecordCache.Expand(version, key);
        return Encoding.UTF8.GetByteCount(expanded) <= ResponseCache.MaxKeyBytes;
    }

    static void Keep(FragmentStore store, string key, byte[] encoded)
    {
        if (encoded.Length <= ResponseCache.MaxEntryBytes)
        {
            store.Write(key, encoded);
        }
    }

    // set_room's block: find_by! the membership, then its room. A missing row raises and is not snapshotted.
    static (Membership Membership, Room Room) LoadMembership(SqliteSession session, long userId, long roomId)
    {
        var membership = Memberships.FindFor(session, userId, roomId) ?? throw new RecordNotFoundException("Couldn't find Membership");
        var room = Rooms.Find(session, membership.RoomId) ?? throw new InvalidOperationException("The membership's room is gone");
        return (membership, room);
    }

    static (Session? Session, User? User) LoadSession(SqliteSession session, string token)
    {
        var row = Sessions.FindByToken(session, token);
        return (row, row is null ? null : Users.Find(session, row.UserId));
    }
}
