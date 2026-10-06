using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/ban.rb
public static class Bans
{
    const string select = $"SELECT {Ban.Columns} FROM \"bans\"";

    // `Ban.banned?(ip_address)`
    public static bool IsBanned(SqliteSession session, string? ipAddress) =>
        Sql.Exists(session, "SELECT 1 AS one FROM \"bans\" WHERE \"bans\".\"ip_address\" " + (ipAddress is null ? "IS NULL" : "= @ip_address") + " LIMIT 1", ("@ip_address", ipAddress));

    // `user.bans`
    public static List<Ban> ForUser(SqliteSession session, long userId) =>
        session.Query($"{select} WHERE \"bans\".\"user_id\" = @user_id", Ban.Read, ("@user_id", userId));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"bans\"");

    // The row of `user.bans.create!(ip_address:)`. The caller validates the address first
    // (`ip_address_is_public`).
    public static Ban Create(SqliteSession session, long userId, string ipAddress, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "bans" ("created_at", "ip_address", "updated_at", "user_id") VALUES (@now, @ip_address, @now, @user_id) RETURNING "id"
            """, ("@now", Db.Time(now)), ("@ip_address", ipAddress), ("@user_id", userId));
        return Sql.One(session, $"{select} WHERE \"bans\".\"id\" = @id LIMIT 1", Ban.Read, ("@id", id))!;
    }

    // `user.bans.delete_all` (`unban`)
    public static int DeleteForUser(SqliteSession session, long userId) =>
        session.Execute("DELETE FROM \"bans\" WHERE \"bans\".\"user_id\" = @user_id", ("@user_id", userId));
}
