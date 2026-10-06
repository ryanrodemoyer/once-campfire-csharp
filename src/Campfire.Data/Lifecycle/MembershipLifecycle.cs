using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Lifecycle;

// Memberships with their callbacks (reference/app/models/membership.rb, room.rb): a destroyed
// membership resets its user's connections once it commits (`after_destroy_commit {
// user.reset_remote_connections }`), so their browser reconnects without the room.
public static class MembershipLifecycle
{
    const string select = $"SELECT {Membership.Columns} FROM \"memberships\"";

    // `membership.destroy`
    public static void Destroy(WriteTransaction transaction, DomainSeams seams, Membership membership)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(membership);
        Memberships.Delete(transaction.Session, membership.Id);
        transaction.AfterCommit(_ => seams.Connections.Disconnect(membership.UserId, reconnect: true));
    }

    // `memberships.revise(granted:, revoked:)`: one transaction granting, then revoking; the
    // revoked users' connections reset after it commits, in the order they were revoked.
    public static void Revise(WriteTransaction transaction, DomainSeams seams, Room room, IReadOnlyList<long> grantedUserIds, IReadOnlyList<long> revokedUserIds)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(grantedUserIds);
        ArgumentNullException.ThrowIfNull(revokedUserIds);
        if (grantedUserIds.Count > 0)
        {
            Memberships.GrantTo(transaction.Session, room, grantedUserIds);
        }
        foreach (var membership in Revoked(transaction.Session, room.Id, revokedUserIds))
        {
            Destroy(transaction, seams, membership);
        }
    }

    // `memberships.revoke_from(users)` outside a transaction: `destroy_by` loads the memberships,
    // then destroys each in a transaction of its own.
    public static async Task RevokeFromAsync(SqliteDatabase database, DomainSeams seams, long roomId, IReadOnlyList<long> userIds)
    {
        ArgumentNullException.ThrowIfNull(database);
        var revoked = await database.ReadAsync(session => Revoked(session, roomId, userIds)).ConfigureAwait(false);
        foreach (var membership in revoked)
        {
            await database.WriteAsync(transaction => Destroy(transaction, seams, membership)).ConfigureAwait(false);
        }
    }

    // `room.memberships.where(user: users)`, in the order `destroy_by` loads them.
    static List<Membership> Revoked(SqliteSession session, long roomId, IReadOnlyList<long> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return [];
        }
        var placeholders = string.Join(", ", userIds.Select((_, i) => $"@user_id{i}"));
        var parameters = userIds.Select((userId, i) => ($"@user_id{i}", (object?)userId)).Prepend(("@room_id", roomId)).ToArray();
        return session.Query($"{select} WHERE \"memberships\".\"room_id\" = @room_id AND \"memberships\".\"user_id\" IN ({placeholders})", Membership.Read, parameters);
    }
}
