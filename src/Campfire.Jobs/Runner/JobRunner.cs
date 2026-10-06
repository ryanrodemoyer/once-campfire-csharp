using System.Diagnostics;
using System.Threading.Channels;
using Campfire.Data.Events;

namespace Campfire.Jobs.Runner;

// The in-process stand-in for Resque (reference/config/environments/production.rb): each job type
// registered with a handler gets a bounded queue and its own workers. `Enqueue` never blocks and
// never runs the job on the caller's thread, so Lifecycle can call it inside a transaction or an
// after-commit callback, as `perform_later` is called in the reference. Like Resque, a job that
// raises is logged and discarded (ApplicationJob doesn't `retry_on`).
public sealed class JobRunner(JobLog log) : IJobQueue, IAsyncDisposable
{
    public static readonly TimeSpan DefaultShutdownDeadline = TimeSpan.FromSeconds(25);

    readonly Dictionary<Type, Queue> queues = [];
    readonly CancellationTokenSource abandon = new();
    readonly Lock gate = new();
    Task<bool>? shutdown;
    int running;

    // Jobs being performed right now, across every queue.
    public int Running => Volatile.Read(ref running);

    // Performs `TJob`s with `perform`, at most `limits.Concurrency` at a time. The token passed
    // to `perform` is cancelled when a shutdown abandons the job at its deadline.
    public void Register<TJob>(Func<TJob, CancellationToken, Task> perform, JobQueueLimits? limits = null) where TJob : Job
    {
        ArgumentNullException.ThrowIfNull(perform);
        limits ??= JobQueueLimits.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.Capacity, 1, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.Concurrency, 1, nameof(limits));
        lock (gate)
        {
            if (shutdown is not null)
            {
                throw new InvalidOperationException("The job runner has shut down.");
            }
            if (queues.ContainsKey(typeof(TJob)))
            {
                throw new InvalidOperationException($"{typeof(TJob).Name} already has a handler.");
            }
            queues[typeof(TJob)] = new Queue(this, limits, (job, cancellationToken) => perform((TJob)job, cancellationToken));
        }
    }

    public void Enqueue(Job job) => TryEnqueue(job);

    // Queues the job, or logs why it can't and returns false: its type has no handler, its
    // queue is full, or the runner is shutting down.
    public bool TryEnqueue(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Queue? queue;
        lock (gate)
        {
            queues.TryGetValue(job.GetType(), out queue);
        }
        if (queue is null)
        {
            log(JobLogLevel.Error, $"No handler for {job.ClassName}, dropping {Describe(job)}", null);
            return false;
        }
        if (queue.Jobs.Writer.TryWrite(job))
        {
            return true;
        }
        if (Volatile.Read(ref shutdown) is not null)
        {
            log(JobLogLevel.Warning, $"Job runner stopped, dropping {Describe(job)}", null);
        }
        else
        {
            log(JobLogLevel.Error, $"Job queue for {job.ClassName} is full ({queue.Limits.Capacity}), dropping {Describe(job)}", null);
        }
        return false;
    }

    // Stops taking jobs, performs the ones already queued and waits for them until `deadline`.
    // Whatever is still running then is told to stop through its cancellation token and left
    // behind, and whatever is still queued is dropped; both are logged. Returns whether every
    // job finished in time.
    public Task<bool> ShutdownAsync(TimeSpan deadline)
    {
        lock (gate)
        {
            shutdown ??= DrainAsync(deadline);
            return shutdown;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Abandoned jobs may still hold the token, so it's only disposed once every job finished.
        if (await ShutdownAsync(DefaultShutdownDeadline).ConfigureAwait(false))
        {
            abandon.Dispose();
        }
    }

    async Task<bool> DrainAsync(TimeSpan deadline)
    {
        List<Queue> all;
        lock (gate)
        {
            all = [.. queues.Values];
        }
        foreach (var queue in all)
        {
            queue.Jobs.Writer.TryComplete();
        }
        var workers = Task.WhenAll(all.SelectMany(queue => queue.Workers));
        try
        {
            await workers.WaitAsync(deadline).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            var left = all.Sum(queue => queue.Jobs.Reader.Count);
            log(JobLogLevel.Warning, $"Job runner shutdown deadline ({deadline.TotalSeconds}s) passed: abandoning {Running} running and {left} queued jobs", null);
            await abandon.CancelAsync().ConfigureAwait(false);
            return false;
        }
    }

    async Task WorkAsync(Queue queue)
    {
        // Once abandoned at the shutdown deadline, the jobs left in the queue are dropped
        // (DrainAsync has logged them): a channel with items ready ignores a cancelled token.
        var reader = queue.Jobs.Reader;
        try
        {
            while (await reader.WaitToReadAsync(abandon.Token).ConfigureAwait(false))
            {
                while (!abandon.IsCancellationRequested && reader.TryRead(out var job))
                {
                    await PerformAsync(queue, job).ConfigureAwait(false);
                }
                abandon.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested)
        {
        }
    }

    async Task PerformAsync(Queue queue, Job job)
    {
        Interlocked.Increment(ref running);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await queue.Perform(job, abandon.Token).ConfigureAwait(false);
            log(JobLogLevel.Information, $"Performed {Describe(job)} in {Elapsed(started)}ms", null);
        }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested)
        {
            log(JobLogLevel.Warning, $"Abandoned {Describe(job)} after {Elapsed(started)}ms", null);
        }
#pragma warning disable CA1031 // A failing job is logged and discarded, as Resque does.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            log(JobLogLevel.Error, $"Error performing {Describe(job)} in {Elapsed(started)}ms: {exception.GetType().Name}: {exception.Message}", exception);
        }
        finally
        {
            Interlocked.Decrement(ref running);
        }
    }

    static string Describe(Job job) => $"{job.ClassName} ({string.Join(", ", job.ArgumentIds)})";

    static string Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    sealed class Queue
    {
        public Queue(JobRunner runner, JobQueueLimits limits, Func<Job, CancellationToken, Task> perform)
        {
            Limits = limits;
            Perform = perform;
            Jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(limits.Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = limits.Concurrency == 1,
            });
            Workers = [.. Enumerable.Range(0, limits.Concurrency).Select(_ => Task.Run(() => runner.WorkAsync(this)))];
        }

        public JobQueueLimits Limits { get; }

        public Func<Job, CancellationToken, Task> Perform { get; }

        public Channel<Job> Jobs { get; }

        public IReadOnlyList<Task> Workers { get; }
    }
}
