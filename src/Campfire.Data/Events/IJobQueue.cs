namespace Campfire.Data.Events;

// `perform_later`: hands the job to the background runner (I01). The reference's
// `config.load_defaults 8.2` turns on Active Job's `enqueue_after_transaction_commit`
// (railties/lib/rails/application/configuration.rb), so a job performed later inside a
// transaction is enqueued once it commits, and not at all if it rolls back: Lifecycle calls this
// from after-commit callbacks (WriteTransaction.AfterCommit) or outside any transaction, and
// implementations enqueue straight away.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The seam's name in plans/csharp-port.md.")]
public interface IJobQueue
{
    void Enqueue(Job job);
}
