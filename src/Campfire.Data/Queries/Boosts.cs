using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/boost.rb
public static class Boosts
{
    const string select = $"SELECT {Boost.Columns} FROM \"boosts\"";

    // `scope :ordered, -> { order(:created_at) }`
    const string ordered = "ORDER BY \"boosts\".\"created_at\" ASC";

    public static Boost? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"boosts\".\"id\" = @id LIMIT 1", Boost.Read, ("@id", id));

    // `message.boosts.find_by!(id:, booster: Current.user)` (messages/boosts_controller.rb)
    public static Boost? FindForBooster(SqliteSession session, long messageId, long id, long boosterId) =>
        Sql.One(session, $"{select} WHERE \"boosts\".\"message_id\" = @message_id AND \"boosts\".\"id\" = @id AND \"boosts\".\"booster_id\" = @booster_id LIMIT 1", Boost.Read,
            ("@message_id", messageId), ("@id", id), ("@booster_id", boosterId));

    // `message.boosts.ordered`
    public static List<Boost> ForMessageOrdered(SqliteSession session, long messageId) =>
        session.Query($"{select} WHERE \"boosts\".\"message_id\" = @message_id {ordered}", Boost.Read, ("@message_id", messageId));

    // The boosts of several messages at once, as `includes(:boosts)` preloads them.
    public static List<Boost> ForMessages(SqliteSession session, IReadOnlyList<long> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("message_id", messageIds);
        return session.Query($"{select} WHERE \"boosts\".\"message_id\" IN ({placeholders})", Boost.Read, parameters);
    }

    // `user.boosts`, which `user.destroy` destroys.
    public static List<Boost> ByBooster(SqliteSession session, long boosterId) =>
        session.Query($"{select} WHERE \"boosts\".\"booster_id\" = @booster_id", Boost.Read, ("@booster_id", boosterId));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"boosts\"");

    // The boost row of `message.boosts.create!(content:)`.
    public static Boost Create(SqliteSession session, long messageId, long boosterId, string content, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "boosts" ("booster_id", "content", "created_at", "message_id", "updated_at") VALUES (@booster_id, @content, @now, @message_id, @now) RETURNING "id"
            """, ("@booster_id", boosterId), ("@content", content), ("@now", Db.Time(now)), ("@message_id", messageId));
        return Find(session, id)!;
    }

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "boosts", id);
}
