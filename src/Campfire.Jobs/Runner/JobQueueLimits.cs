namespace Campfire.Jobs.Runner;

// How many jobs of one type may wait to run (beyond that, enqueues are logged and rejected
// rather than growing the queue), and how many of them run at once.
public sealed record JobQueueLimits(int Capacity = JobQueueLimits.DefaultCapacity, int Concurrency = 1)
{
    public const int DefaultCapacity = 1024;

    public static JobQueueLimits Default { get; } = new();
}
