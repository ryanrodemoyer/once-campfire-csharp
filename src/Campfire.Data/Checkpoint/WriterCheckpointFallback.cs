using Campfire.Data.Sqlite;

namespace Campfire.Data.Checkpoint;

// The writer's own PASSIVE checkpoint, for when the background contender (SqliteWalCheckpoint) can't
// keep the WAL small. Under a steady stream of commits a contender's checkpoint never catches up
// between two of them, so the WAL never restarts and keeps growing. The writer connection turns
// SQLite's auto-checkpoint back on at its default of 1,000 pages: SQLite counts the frames in the
// WAL on each commit and checkpoints only once there are that many, which lets the next transaction
// restart the WAL. Every other connection keeps database.yml's wal_autocheckpoint = 0.
//
// It counts frames, not the WAL file's size: SQLite reuses the file from the start after a restart
// and never shrinks it below journal_size_limit, so once the file passed the threshold, a size
// check would checkpoint (and fsync) after every commit.
public static class WriterCheckpointFallback
{
    // SQLite's default wal_autocheckpoint.
    public const int Pages = 1000;

    // Pages of 4 KiB.
    public const long ThresholdBytes = Pages * 4096L;

    public static void Enable(SqliteSession writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Execute($"PRAGMA wal_autocheckpoint = {Pages}");
    }
}
