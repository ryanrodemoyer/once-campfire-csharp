using Microsoft.Data.Sqlite;

namespace Campfire.Data.Sqlite;

// Prepared statements for one connection, keyed by SQL and evicted least recently used first,
// like Active Record's per-connection StatementPool (`statement_limit`, 1000 by default).
// Not thread-safe: a connection is only ever used by one thread at a time.
sealed class StatementCache(SqliteConnection connection, int capacity) : IDisposable
{
    public const int DefaultCapacity = 1000;

    readonly Dictionary<string, LinkedListNode<SqliteCommand>> commands = new(StringComparer.Ordinal);
    readonly LinkedList<SqliteCommand> recency = new();

    public int Count => commands.Count;

    // The command for `sql`, prepared on first use, with its parameters cleared. It stays owned
    // by the cache: callers must not dispose it, and must finish with any reader it returned
    // before asking for the same SQL again.
    public SqliteCommand Get(string sql)
    {
        if (commands.TryGetValue(sql, out var node))
        {
            recency.Remove(node);
            recency.AddFirst(node);
            node.Value.Parameters.Clear();
            return node.Value;
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            command.Prepare();
        }
        catch
        {
            command.Dispose();
            throw;
        }

        commands[sql] = recency.AddFirst(command);
        if (commands.Count > capacity)
        {
            Evict(recency.Last!);
        }
        return command;
    }

    void Evict(LinkedListNode<SqliteCommand> node)
    {
        recency.Remove(node);
        commands.Remove(node.Value.CommandText);
        node.Value.Dispose();
    }

    public void Dispose()
    {
        foreach (var command in recency)
        {
            command.Dispose();
        }
        recency.Clear();
        commands.Clear();
    }
}
