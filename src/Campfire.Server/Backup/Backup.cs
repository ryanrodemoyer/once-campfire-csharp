using Campfire.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Campfire.Server.Cli;

/// <summary>
/// <c>campfire backup</c> is <c>reference/script/admin/prepare-backup</c>, which the ONCE pre-backup
/// hook runs while the app keeps serving. It is an SQLite online backup of the whole database, in
/// one step, into <c>storage/backups/&lt;database file name&gt;</c>. The post-restore hook
/// (<c>reference/hooks/post-restore</c>) copies that file back over the database and drops its WAL.
/// </summary>
public static partial class Commands
{
    static partial void Backup(ServerSettings settings, TextWriter error, ref int? exitCode)
    {
        try
        {
            CreateBackup(settings);
            exitCode = 0;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"campfire backup: {exception.Message}");
            exitCode = 1;
        }
    }

    /// <summary>
    /// <c>Backup.create</c>: <c>SQLite3::Backup.new(dest, "main", source, "main")</c>, then
    /// <c>step(-1)</c> and <c>finish</c>. The source is opened as Active Record opens it, in WAL mode,
    /// so the backup reads one consistent snapshot while other connections go on writing. The
    /// destination is a plain <c>SQLite3::Database.new</c>, with SQLite's defaults.
    /// </summary>
    /// <returns>The backup's path.</returns>
    public static string CreateBackup(ServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // `Rails.root.join("storage", "backups").tap(&:mkpath)`, and Room.connection, which creates
        // the database's directory as the sqlite3 adapter does.
        Directory.CreateDirectory(settings.BackupsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        var backupPath = Path.Combine(settings.BackupsPath, Path.GetFileName(settings.DatabasePath));

        using var source = SqliteConnections.Open(settings.DatabasePath);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Pooling = false,
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        return backupPath;
    }
}
