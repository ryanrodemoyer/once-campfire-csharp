using Campfire.Data.Sqlite;

namespace Campfire.Data.Checkpoint;

// The writer's own PASSIVE checkpoint, for when the background contender (SqliteWalCheckpoint) can't
// keep the WAL small. Under a steady stream of commits a contender's checkpoint never catches up
// between two of them, so the WAL never restarts and keeps growing. Once the WAL passes SQLite's
// default auto-checkpoint size, the writer checkpoints between commits: the work SQLite would have
// done itself, and it lets the next transaction restart the WAL.
public static class WriterCheckpointFallback
{
    // 1,000 pages of 4 KiB, SQLite's default wal_autocheckpoint.
    public const long ThresholdBytes = 4 * 1024 * 1024;

    // Runs on the writer thread after each commit, so it only looks at the WAL's size.
    public static void CheckpointIfLarge(SqliteSession writer, string databasePath)
    {
        var wal = new FileInfo($"{databasePath}-wal");
        if (wal.Exists && wal.Length > ThresholdBytes)
        {
            writer.Execute("PRAGMA wal_checkpoint(PASSIVE)");
        }
    }
}
