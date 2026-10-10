using System.Collections.Immutable;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Schema;

// reference/app/models/room/messages_count.rb
// Keeps rooms.messages_count correct for ordinary SQLite writes — Rails, bulk SQL,
// and foreign connections — without ActiveRecord counter_cache callbacks.
public static class RoomMessagesCount
{
    public const string InsertTrigger = "messages_ai_rooms_messages_count";
    public const string DeleteTrigger = "messages_ad_rooms_messages_count";
    public const string UpdateTrigger = "messages_au_rooms_messages_count";

    public static readonly ImmutableArray<string> Triggers =
    [
        InsertTrigger,
        DeleteTrigger,
        UpdateTrigger,
    ];

    public static void Install(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        WithImmediateWrite(session, () => ReplaceTriggers(session));
    }

    public static void Uninstall(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (var name in Triggers)
        {
            session.Execute($"DROP TRIGGER IF EXISTS {name}");
        }
    }

    public static void Backfill(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Execute("""
            UPDATE rooms SET messages_count = (
              SELECT COUNT(*) FROM messages WHERE messages.room_id = rooms.id
            )
            """);
    }

    public static void Ensure(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TableExists(session, "rooms"))
        {
            return;
        }
        if (!ColumnExists(session, "rooms", "messages_count"))
        {
            return;
        }
        if (TriggersInstalled(session))
        {
            return;
        }

        WithImmediateWrite(session, () =>
        {
            if (TriggersInstalled(session))
            {
                return;
            }

            Uninstall(session);
            Backfill(session);
            CreateTriggers(session);
        });
    }

    public static bool TriggerInstalled(SqliteSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Scalar<long>("SELECT 1 FROM sqlite_master WHERE type = 'trigger' AND name = @name", ("@name", name)) == 1;
    }

    public static bool TriggersInstalled(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Triggers.All(name => TriggerInstalled(session, name));
    }

    static void ReplaceTriggers(SqliteSession session)
    {
        Uninstall(session);
        CreateTriggers(session);
    }

    static void CreateTriggers(SqliteSession session)
    {
        session.Execute($"""
            CREATE TRIGGER {InsertTrigger} AFTER INSERT ON messages
            BEGIN
              UPDATE rooms SET messages_count = messages_count + 1 WHERE id = NEW.room_id;
            END
            """);
        session.Execute($"""
            CREATE TRIGGER {DeleteTrigger} AFTER DELETE ON messages
            BEGIN
              UPDATE rooms SET messages_count = messages_count - 1 WHERE id = OLD.room_id;
            END
            """);
        session.Execute($"""
            CREATE TRIGGER {UpdateTrigger} AFTER UPDATE OF room_id ON messages
            WHEN OLD.room_id IS NOT NEW.room_id
            BEGIN
              UPDATE rooms SET messages_count = messages_count - 1 WHERE id = OLD.room_id;
              UPDATE rooms SET messages_count = messages_count + 1 WHERE id = NEW.room_id;
            END
            """);
    }

    static bool TableExists(SqliteSession session, string name) =>
        session.Scalar<long>("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name", ("@name", name)) == 1;

    static bool ColumnExists(SqliteSession session, string table, string column) =>
        session.Query($"PRAGMA table_info(\"{table}\")", reader => reader.GetString(1))
            .Any(col => string.Equals(col, column, StringComparison.OrdinalIgnoreCase));

    static void WithImmediateWrite(SqliteSession session, Action action)
    {
        if (SQLitePCL.raw.sqlite3_get_autocommit(session.Connection.Handle) == 0)
        {
            action();
        }
        else
        {
            WriteTransaction.Run(session, _ =>
            {
                action();
                return true;
            });
        }
    }
}
