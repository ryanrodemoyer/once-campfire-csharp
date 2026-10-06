using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/session.rb
public static class Sessions
{
    const string select = $"SELECT {Session.Columns} FROM \"sessions\"";

    public static Session? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"sessions\".\"id\" = @id LIMIT 1", Session.Read, ("@id", id));

    // `Session.find_by(token:)` (concerns/authentication/session_lookup.rb)
    public static Session? FindByToken(SqliteSession session, string token) =>
        Sql.One(session, $"{select} WHERE \"sessions\".\"token\" = @token LIMIT 1", Session.Read, ("@token", token));

    // `user.sessions`
    public static List<Session> ForUser(SqliteSession session, long userId) =>
        session.Query($"{select} WHERE \"sessions\".\"user_id\" = @user_id", Session.Read, ("@user_id", userId));

    public static long CountForUser(SqliteSession session, long userId) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"sessions\" WHERE \"sessions\".\"user_id\" = @user_id", ("@user_id", userId));

    // `sessions.pluck(:ip_address).compact_blank.uniq` (user/bannable.rb)
    public static List<string> IpAddressesForUser(SqliteSession session, long userId) =>
        session.Query("SELECT \"sessions\".\"ip_address\" FROM \"sessions\" WHERE \"sessions\".\"user_id\" = @user_id", reader => Db.ReadNullableString(reader, 0), ("@user_id", userId))
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // `user.sessions.start!(user_agent:, ip_address:)`: a new token, active now.
    public static Session Start(SqliteSession session, long userId, string? userAgent, string? ipAddress, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "sessions" ("created_at", "ip_address", "last_active_at", "token", "updated_at", "user_agent", "user_id") VALUES (@now, @ip_address, @now, @token, @now, @user_agent, @user_id) RETURNING "id"
            """, ("@now", Db.Time(now)), ("@ip_address", ipAddress), ("@token", Session.GenerateToken()), ("@user_agent", userAgent), ("@user_id", userId));
        return Find(session, id)!;
    }

    // `resume(user_agent:, ip_address:)`: once an hour, records the browser and its activity.
    public static Session Resume(SqliteSession session, Session record, string? userAgent, string? ipAddress, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.NeedsActivityRefresh(now))
        {
            return record;
        }
        var changes = new List<(string, object?)>();
        if (ipAddress != record.IpAddress)
        {
            changes.Add(("ip_address", ipAddress));
        }
        changes.Add(("last_active_at", Db.Time(now)));
        if (userAgent != record.UserAgent)
        {
            changes.Add(("user_agent", userAgent));
        }
        Sql.UpdateChanged(session, "sessions", record.Id, changes, now);
        return Find(session, record.Id)!;
    }

    // `session.destroy`
    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "sessions", id);

    // `user.sessions.delete_all`
    public static int DeleteForUser(SqliteSession session, long userId) =>
        session.Execute("DELETE FROM \"sessions\" WHERE \"sessions\".\"user_id\" = @user_id", ("@user_id", userId));
}
