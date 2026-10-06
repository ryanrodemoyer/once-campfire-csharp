using System.Collections.Concurrent;
using Campfire.Data.Events;
using Campfire.Jobs.Runner;

namespace Campfire.Jobs.Tests.Runner;

public sealed class JobRunnerTests
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    sealed record TestJob(long Id) : Job
    {
        public override string ClassName => "TestJob";

        public override IReadOnlyList<long> ArgumentIds => [Id];
    }

    sealed class Log
    {
        readonly ConcurrentQueue<(JobLogLevel Level, string Message, Exception? Exception)> entries = new();

        public JobLog Writer => (level, message, exception) => entries.Enqueue((level, message, exception));

        public IReadOnlyList<(JobLogLevel Level, string Message, Exception? Exception)> Entries => [.. entries];

        public IReadOnlyList<string> At(JobLogLevel level) => [.. entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];
    }

    static async Task Eventually(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time: " + describe?.Invoke());
            await Task.Delay(5, Cancellation);
        }
    }

    [Fact]
    public async Task Enqueue_returns_at_once_and_the_job_runs_in_the_background()
    {
        var log = new Log();
        await using var runner = new JobRunner(log.Writer);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var performed = new TaskCompletionSource<TestJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Register<TestJob>(async (job, _) =>
        {
            await release.Task;
            performed.SetResult(job);
        });

        // Enqueue returns while the job is still waiting to finish, so it didn't run inline.
        runner.Enqueue(new TestJob(7));
        Assert.False(performed.Task.IsCompleted);
        release.SetResult();

        Assert.Equal(new TestJob(7), await performed.Task.WaitAsync(Patience, Cancellation));
        await Eventually(() => log.At(JobLogLevel.Information).Count == 1);
        Assert.StartsWith("Performed TestJob (7) in ", log.At(JobLogLevel.Information)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_queue_logs_and_rejects_instead_of_growing()
    {
        var log = new Log();
        await using var runner = new JobRunner(log.Writer);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var performed = new ConcurrentQueue<long>();
        runner.Register<TestJob>(async (job, _) =>
        {
            await release.Task;
            performed.Enqueue(job.Id);
        }, new JobQueueLimits(Capacity: 2, Concurrency: 1));

        Assert.True(runner.TryEnqueue(new TestJob(1)));
        await Eventually(() => runner.Running == 1);
        Assert.True(runner.TryEnqueue(new TestJob(2)));
        Assert.True(runner.TryEnqueue(new TestJob(3)));
        Assert.False(runner.TryEnqueue(new TestJob(4)));
        runner.Enqueue(new TestJob(5));

        Assert.Equal(
            ["Job queue for TestJob is full (2), dropping TestJob (4)", "Job queue for TestJob is full (2), dropping TestJob (5)"],
            log.At(JobLogLevel.Error));

        release.SetResult();
        Assert.True(await runner.ShutdownAsync(Patience));
        Assert.Equal([1, 2, 3], performed.Order());
    }

    [Fact]
    public async Task Each_type_runs_at_most_its_concurrency_at_once()
    {
        await using var runner = new JobRunner(new Log().Writer);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var peak = 0;
        var done = 0;
        runner.Register<TestJob>(async (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await release.Task;
            Interlocked.Decrement(ref current);
            Interlocked.Increment(ref done);
        }, new JobQueueLimits(Capacity: 10, Concurrency: 2));
        var otherRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Register<RemoveBannedContentJob>((_, _) =>
        {
            otherRan.SetResult();
            return Task.CompletedTask;
        });

        for (var i = 0; i < 5; i++)
        {
            runner.Enqueue(new TestJob(i));
        }
        await Eventually(() => Volatile.Read(ref current) == 2);
        // Another type has its own queue and workers, so it isn't stuck behind the busy one.
        runner.Enqueue(new RemoveBannedContentJob(1));
        await otherRan.Task.WaitAsync(Patience, Cancellation);
        await Task.Delay(50, Cancellation);
        Assert.Equal(2, Volatile.Read(ref current));

        release.SetResult();
        Assert.True(await runner.ShutdownAsync(Patience));
        Assert.Equal(5, done);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Shutdown_drains_running_and_queued_jobs_within_the_deadline()
    {
        var log = new Log();
        var runner = new JobRunner(log.Writer);
        var performed = new ConcurrentQueue<long>();
        runner.Register<TestJob>(async (job, cancellationToken) =>
        {
            await Task.Delay(20, cancellationToken);
            performed.Enqueue(job.Id);
        }, new JobQueueLimits(Capacity: 10, Concurrency: 2));
        for (var i = 0; i < 6; i++)
        {
            runner.Enqueue(new TestJob(i));
        }

        Assert.True(await runner.ShutdownAsync(Patience));

        Assert.Equal([0, 1, 2, 3, 4, 5], performed.Order());
        Assert.Equal(0, runner.Running);
        Assert.Empty(log.At(JobLogLevel.Warning));

        Assert.False(runner.TryEnqueue(new TestJob(9)));
        Assert.Equal(["Job runner stopped, dropping TestJob (9)"], log.At(JobLogLevel.Warning));
        Assert.Same(runner.ShutdownAsync(Patience), runner.ShutdownAsync(TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => runner.Register<PushMessageJob>((_, _) => Task.CompletedTask));
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_abandons_what_is_still_running_at_the_deadline()
    {
        var log = new Log();
        var runner = new JobRunner(log.Writer);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var performed = new ConcurrentQueue<long>();
        runner.Register<TestJob>(async (job, cancellationToken) =>
        {
            performed.Enqueue(job.Id);
            started.TrySetResult();
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });
        runner.Enqueue(new TestJob(1));
        runner.Enqueue(new TestJob(2));
        await started.Task.WaitAsync(Patience, Cancellation);

        Assert.False(await runner.ShutdownAsync(TimeSpan.FromMilliseconds(100)));

        await cancelled.Task.WaitAsync(Patience, Cancellation);
        await Eventually(() => log.At(JobLogLevel.Warning).Count == 2, () => string.Join(" / ", log.Entries.Select(e => e.Message)));
        Assert.Equal("Job runner shutdown deadline (0.1s) passed: abandoning 1 running and 1 queued jobs", log.At(JobLogLevel.Warning)[0]);
        Assert.StartsWith("Abandoned TestJob (1) after ", log.At(JobLogLevel.Warning)[1], StringComparison.Ordinal);
        await Eventually(() => runner.Running == 0);
        Assert.Equal([1], performed);
    }

    [Fact]
    public async Task A_failing_job_is_logged_and_the_queue_carries_on()
    {
        var log = new Log();
        await using var runner = new JobRunner(log.Writer);
        var performed = new ConcurrentQueue<long>();
        runner.Register<TestJob>((job, _) =>
        {
            if (job.Id == 1)
            {
                throw new InvalidOperationException("boom");
            }
            performed.Enqueue(job.Id);
            return Task.CompletedTask;
        });

        runner.Enqueue(new TestJob(1));
        runner.Enqueue(new TestJob(2));
        Assert.True(await runner.ShutdownAsync(Patience));

        Assert.Equal([2], performed);
        var error = Assert.Single(log.Entries, entry => entry.Level == JobLogLevel.Error);
        Assert.StartsWith("Error performing TestJob (1) in ", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(": InvalidOperationException: boom", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task A_job_without_a_handler_is_logged_and_rejected()
    {
        var log = new Log();
        await using var runner = new JobRunner(log.Writer);
        runner.Register<TestJob>((_, _) => Task.CompletedTask);

        Assert.False(runner.TryEnqueue(new WebhookJob(3, 4)));

        Assert.Equal(["No handler for Bot::WebhookJob, dropping Bot::WebhookJob (3, 4)"], log.At(JobLogLevel.Error));
        Assert.Throws<InvalidOperationException>(() => runner.Register<TestJob>((_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Register<PushMessageJob>((_, _) => Task.CompletedTask, new JobQueueLimits(Capacity: 0)));
    }

    static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
            {
                return;
            }
            seen = previous;
        }
    }
}
