using System.Diagnostics;
using Campfire.Data.Sqlite;
using Campfire.Server.Cli;
using Campfire.Server.Tests.Cli;
using Microsoft.Data.Sqlite;

namespace Campfire.Server.Tests.Backup;

public sealed class BackupTests : IDisposable
{
    readonly TestRoot root = new();
    readonly StringWriter output = new();
    readonly StringWriter error = new();

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        root.Dispose();
    }

    string BackupPath => Path.Combine(root.Path, "storage", "backups", "production.sqlite3");

    Task<int> Backup(ServerSettings settings) => Commands.RunAsync(["backup"], settings, output, error);

    [Fact]
    public async Task Backup_copies_the_database_into_storage_backups()
    {
        var database = root.CopySeed();

        Assert.Equal(0, await Backup(root.Settings()));

        Assert.Equal("", error.ToString());
        Assert.Equal(Dump(database), Dump(BackupPath));
        Assert.Equal("ok", Scalar(BackupPath, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task Backup_is_named_after_the_environments_database()
    {
        root.CopySeed();
        File.Move(Path.Combine(root.Path, "storage", "db", "production.sqlite3"), Path.Combine(root.Path, "storage", "db", "staging.sqlite3"));

        Assert.Equal(0, await Backup(root.Settings(("RAILS_ENV", "staging"))));

        Assert.True(File.Exists(Path.Combine(root.Path, "storage", "backups", "staging.sqlite3")));
    }

    [Fact]
    public async Task Backup_replaces_the_previous_backup()
    {
        var database = root.CopySeed();
        Assert.Equal(0, await Backup(root.Settings()));
        Execute(database, "UPDATE rooms SET name = 'Renamed'");

        Assert.Equal(0, await Backup(root.Settings()));

        Assert.Equal(0L, Scalar(BackupPath, "SELECT COUNT(*) FROM rooms WHERE name IS NOT 'Renamed'"));
    }

    [Fact]
    public async Task Backup_reports_a_database_it_cannot_read()
    {
        var database = Path.Combine(root.Path, "storage", "db", "production.sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await File.WriteAllTextAsync(database, "not a database", TestContext.Current.CancellationToken);

        Assert.Equal(1, await Backup(root.Settings()));

        Assert.StartsWith("campfire backup: SQLite Error 26: ", error.ToString(), StringComparison.Ordinal);
    }

    // The acceptance test: a backup taken while another connection commits a steady stream of
    // two-message transactions holds a whole number of transactions, and once the post-restore
    // hook puts it back, the database passes SQLite's checks and the app's db:prepare boots on it.
    [Fact]
    public async Task Backup_during_a_write_load_restores_to_a_consistent_database()
    {
        var database = root.CopySeed();
        var (roomId, userId) = ((long)Scalar(database, "SELECT MIN(id) FROM rooms")!, (long)Scalar(database, "SELECT MIN(id) FROM users")!);
        using var writes = new CancellationTokenSource();
        var committed = 0;
        var writer = Task.Run(() =>
        {
            using var connection = SqliteConnections.Open(database);
            while (!writes.IsCancellationRequested)
            {
                using var transaction = connection.BeginTransaction();
                for (var i = 0; i < 2; i++)
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO messages (client_message_id, created_at, creator_id, room_id, updated_at)
                        VALUES ('backup-load', CURRENT_TIMESTAMP, $user, $room, CURRENT_TIMESTAMP)
                        """;
                    insert.Parameters.AddWithValue("$user", userId);
                    insert.Parameters.AddWithValue("$room", roomId);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
                Interlocked.Increment(ref committed);
            }
        }, TestContext.Current.CancellationToken);

        await WaitFor(() => Volatile.Read(ref committed) >= 50);
        Assert.Equal(0, await Backup(root.Settings()));
        var committedAfterBackup = Volatile.Read(ref committed);
        await WaitFor(() => Volatile.Read(ref committed) >= committedAfterBackup + 50);
        await writes.CancelAsync();
        await writer;

        var backedUp = (long)Scalar(BackupPath, "SELECT COUNT(*) FROM messages WHERE client_message_id = 'backup-load'")!;
        Assert.InRange(backedUp, 2 * 50, 2 * committedAfterBackup);
        Assert.Equal(0, backedUp % 2);

        PostRestore();

        Assert.False(File.Exists(database + "-wal"));
        Assert.Equal(backedUp, Scalar(database, "SELECT COUNT(*) FROM messages WHERE client_message_id = 'backup-load'"));
        Assert.Equal("ok", Scalar(database, "PRAGMA integrity_check"));
        Assert.Null(Scalar(database, "PRAGMA foreign_key_check"));
        Assert.Equal(0, await Commands.RunAsync(["db:prepare"], root.Settings(), output, error));
        Assert.Equal("", error.ToString());
    }

    // reference/hooks/post-restore, run as it is with /rails moved to the test's root.
    void PostRestore()
    {
        if (OperatingSystem.IsWindows())
        {
            File.Copy(BackupPath, Path.Combine(root.Path, "storage", "db", "production.sqlite3"), overwrite: true);
            return;
        }

        var script = Path.Combine(root.Path, "post-restore");
        File.WriteAllText(script, File.ReadAllText(Path.Combine(TestRoot.RepositoryRoot, "reference", "hooks", "post-restore"))
            .Replace("/rails/", root.Path + "/", StringComparison.Ordinal));
        using var hook = Process.Start(new ProcessStartInfo("bash", [script]) { RedirectStandardError = true })!;
        var stderr = hook.StandardError.ReadToEnd();
        hook.WaitForExit();
        Assert.True(hook.ExitCode == 0, stderr);
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "timed out waiting for the writer");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    // Every table's rows, in order, so two databases can be compared.
    static List<string> Dump(string database)
    {
        using var connection = Connect(database);
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var rows = new List<string>();
        foreach (var table in tables)
        {
            using var select = connection.CreateCommand();
            // A backup copies pages, so rows come back in the same order.
            select.CommandText = $"SELECT * FROM \"{table}\"";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(table + ": " + string.Join("|", values.Select(value => value is byte[] bytes ? Convert.ToHexString(bytes) : value.ToString())));
            }
        }
        return rows;
    }

    static object? Scalar(string database, string sql)
    {
        using var connection = Connect(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    static void Execute(string database, string sql)
    {
        using var connection = Connect(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static SqliteConnection Connect(string database)
    {
        var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        return connection;
    }
}
