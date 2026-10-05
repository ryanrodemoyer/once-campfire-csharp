namespace Campfire.Data.Sqlite;

// A write in progress on the writer's session, inside `BEGIN IMMEDIATE TRANSACTION`
// (`default_transaction_mode: immediate` in reference/config/database.yml). Work queued with
// AfterCommit runs in order once the transaction commits, outside it, the way Active Record runs
// `after_commit` callbacks; it is dropped if the transaction rolls back.
public sealed class WriteTransaction
{
    readonly List<Action<SqliteSession>> afterCommit = [];

    internal WriteTransaction(SqliteSession session) => Session = session;

    public SqliteSession Session { get; }

    public void AfterCommit(Action<SqliteSession> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        afterCommit.Add(work);
    }

    // Runs `work` in an immediate transaction, commits, then runs the after-commit work. Every
    // after-commit action runs even if an earlier one throws; the first exception is rethrown
    // once they all have, as the committed write's failure.
    internal static T Run<T>(SqliteSession session, Func<WriteTransaction, T> work)
    {
        var transaction = new WriteTransaction(session);
        session.Execute("BEGIN IMMEDIATE TRANSACTION");
        T value;
        try
        {
            value = work(transaction);
            session.Execute("COMMIT TRANSACTION");
        }
        catch
        {
            RollBackIfOpen(session);
            throw;
        }

        Exception? first = null;
        foreach (var action in transaction.afterCommit)
        {
            try
            {
                action(session);
            }
#pragma warning disable CA1031 // The remaining after-commit work still runs; the first failure is rethrown below.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                first ??= exception;
            }
        }
        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }
        return value;
    }

    // A failed COMMIT, or a statement error SQLite answers by rolling back on its own
    // (SQLITE_FULL, SQLITE_IOERR, ...), can leave no transaction to roll back.
    static void RollBackIfOpen(SqliteSession session)
    {
        if (SQLitePCL.raw.sqlite3_get_autocommit(session.Connection.Handle) == 0)
        {
            session.Execute("ROLLBACK TRANSACTION");
        }
    }
}
