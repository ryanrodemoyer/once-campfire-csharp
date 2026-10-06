using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// The rows of reference/app/models/search.rb. Recording a search (`Search.record`) is D04's.
public static class Searches
{
    const string select = $"SELECT {Search.Columns} FROM \"searches\"";

    // `scope :ordered, -> { order(updated_at: :desc) }`
    const string ordered = "ORDER BY \"searches\".\"updated_at\" DESC";

    // `user.searches.ordered`
    public static List<Search> ForUserOrdered(SqliteSession session, long userId) =>
        session.Query($"{select} WHERE \"searches\".\"user_id\" = @user_id {ordered}", Search.Read, ("@user_id", userId));

    // `user.searches.find_by(query:)`
    public static Search? FindForUser(SqliteSession session, long userId, string query) =>
        Sql.One(session, $"{select} WHERE \"searches\".\"user_id\" = @user_id AND \"searches\".\"query\" = @query LIMIT 1", Search.Read, ("@user_id", userId), ("@query", query));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"searches\"");

    public static long CountForUser(SqliteSession session, long userId) =>
        Sql.Count(session, "SELECT COUNT(*) FROM \"searches\" WHERE \"searches\".\"user_id\" = @user_id", ("@user_id", userId));

    // The row of `user.searches.create!(query:)`.
    public static Search Create(SqliteSession session, long userId, string query, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "searches" ("created_at", "query", "updated_at", "user_id") VALUES (@now, @query, @now, @user_id) RETURNING "id"
            """, ("@now", Db.Time(now)), ("@query", query), ("@user_id", userId));
        return Sql.One(session, $"{select} WHERE \"searches\".\"id\" = @id LIMIT 1", Search.Read, ("@id", id))!;
    }

    public static void Touch(SqliteSession session, long id, DateTimeOffset now) => Sql.Touch(session, "searches", id, now);

    // `user.searches.excluding(user.searches.ordered.limit(10)).destroy_all`: all but the ten
    // most recent.
    public static int TrimRecent(SqliteSession session, long userId) =>
        session.Execute(
            $"DELETE FROM \"searches\" WHERE \"searches\".\"user_id\" = @user_id AND \"searches\".\"id\" NOT IN (SELECT \"searches\".\"id\" FROM \"searches\" WHERE \"searches\".\"user_id\" = @user_id {ordered} LIMIT @limit)",
            ("@user_id", userId), ("@limit", Search.RecentLimit));

    // `user.searches.delete_all`
    public static int DeleteForUser(SqliteSession session, long userId) =>
        session.Execute("DELETE FROM \"searches\" WHERE \"searches\".\"user_id\" = @user_id", ("@user_id", userId));
}
