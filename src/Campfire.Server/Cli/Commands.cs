using Campfire.Data.Schema;
using Campfire.Data.Sqlite;

namespace Campfire.Server.Cli;

/// <summary>
/// <c>campfire [server|db:prepare|backup]</c>. <c>server</c> (the default) is the reference's
/// <c>bin/start-app</c>: <c>db:prepare</c>, then the app. <c>backup</c> is
/// <c>script/admin/prepare-backup</c>, which the ONCE pre-backup hook runs.
/// </summary>
public static partial class Commands
{
    public const string Usage = "usage: campfire [server|db:prepare|backup] [server options]";

    // sysexits.h EX_USAGE
    const int usageError = 64;

    public static async Task<int> RunAsync(string[] args, ServerSettings settings, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Options without a command (`campfire --urls ...`) are the server's.
        var (command, rest) = args is [var first, .. var others] && (!first.StartsWith('-') || first is "--help" or "-h")
            ? (first, others)
            : ("server", args);
        try
        {
            switch (command)
            {
                case "server":
                    await ServerCommand.RunAsync(settings, rest).ConfigureAwait(false);
                    return 0;
                case "db:prepare" when rest.Length == 0:
                    Close(PrepareDatabase(settings));
                    return 0;
                case "backup" when rest.Length == 0:
                    return RunBackup(settings, error);
                case "help" or "--help" or "-h":
                    await output.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 0;
                default:
                    await error.WriteLineAsync(Usage).ConfigureAwait(false);
                    return usageError;
            }
        }
        catch (Exception exception) when (exception is SettingsException or PendingMigrationsException)
        {
            await error.WriteLineAsync($"campfire {command}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// <c>bin/rails db:prepare</c>: opens the database, creating its directory and loading the
    /// schema into an empty one, and refuses one with missing migrations.
    /// </summary>
    public static SqliteDatabase PrepareDatabase(ServerSettings settings, int readers = 1)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // The sqlite3 adapter creates the database's directory (SQLite3Adapter.new_client).
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        return SqliteDatabase.Open(new SqliteDatabaseOptions(settings.DatabasePath)
        {
            Environment = settings.RailsEnv,
            Readers = readers,
        });
    }

    /// <summary>
    /// Closes the database and leaves the file as Rails leaves it on exit. SQLite checkpoints the
    /// WAL into the database file and removes it when the last connection closes, but only if that
    /// connection can write, and the database closes its read-only readers last.
    /// </summary>
    public static void Close(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        database.Dispose();
        SqliteConnections.Open(database.Path).Dispose();
    }

    static int RunBackup(ServerSettings settings, TextWriter error)
    {
        int? exitCode = null;
        Backup(settings, error, ref exitCode);
        if (exitCode is { } code)
        {
            return code;
        }

        error.WriteLine("campfire backup: not implemented yet (task P04)");
        return 1;
    }

    /// <summary>
    /// <c>campfire backup</c>: an SQLite online backup of the database into
    /// <see cref="ServerSettings.BackupsPath"/>. P04 implements this hook in <c>Backup/</c>.
    /// </summary>
    static partial void Backup(ServerSettings settings, TextWriter error, ref int? exitCode);
}
