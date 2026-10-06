using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/webhook.rb and the bot's `has_one :webhook` (user/bot.rb)
public static class Webhooks
{
    const string select = $"SELECT {Webhook.Columns} FROM \"webhooks\"";

    public static Webhook? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"webhooks\".\"id\" = @id LIMIT 1", Webhook.Read, ("@id", id));

    // `user.webhook`
    public static Webhook? ForUser(SqliteSession session, long userId) =>
        Sql.One(session, $"{select} WHERE \"webhooks\".\"user_id\" = @user_id LIMIT 1", Webhook.Read, ("@user_id", userId));

    // `user.create_webhook!(url:)`
    public static Webhook Create(SqliteSession session, long userId, string? url, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "webhooks" ("created_at", "updated_at", "url", "user_id") VALUES (@now, @now, @url, @user_id) RETURNING "id"
            """, ("@now", Db.Time(now)), ("@url", url), ("@user_id", userId));
        return Find(session, id)!;
    }

    // `webhook.update!(url:)`
    public static Webhook UpdateUrl(SqliteSession session, Webhook webhook, string? url, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return Sql.UpdateChanged(session, "webhooks", webhook.Id, url == webhook.Url ? [] : [("url", url)], now) ? Find(session, webhook.Id)! : webhook;
    }

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "webhooks", id);

    // `has_one :webhook, dependent: :delete`
    public static int DeleteForUser(SqliteSession session, long userId) =>
        session.Execute("DELETE FROM \"webhooks\" WHERE \"webhooks\".\"user_id\" = @user_id", ("@user_id", userId));
}
