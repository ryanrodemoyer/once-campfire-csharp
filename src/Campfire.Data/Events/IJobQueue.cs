namespace Campfire.Data.Events;

// `perform_later`: hands the job to the background runner (I01). Lifecycle calls it exactly
// where the reference's `perform_later` runs: from after-commit callbacks for a new message,
// but inside the transaction for a ban. The reference leaves Active Job's
// `enqueue_after_transaction_commit` at its default, false, so a job is enqueued at once even
// mid-transaction (activejob/lib/active_job/enqueuing.rb); implementations enqueue straight
// away too.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The seam's name in plans/csharp-port.md.")]
public interface IJobQueue
{
    void Enqueue(Job job);
}
