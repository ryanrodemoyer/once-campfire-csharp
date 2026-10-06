using System.Threading.Channels;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>WebPush::Pool</c> (reference/lib/web_push/pool.rb) and its configuration in
/// reference/config/initializers/web_push.rb: up to 50 deliveries at once with 10,000 more
/// waiting (more are dropped without a word), and one worker that destroys the subscriptions a
/// delivery found dead: a 410 (<c>WebPush::ExpiredSubscription</c>) or any OpenSSL error.
/// </summary>
public sealed class WebPushPool : IAsyncDisposable
{
    /// <summary>The delivery pool's <c>max_threads</c>.</summary>
    public const int MaxThreads = 50;

    /// <summary>The delivery pool's <c>max_queue</c>.</summary>
    public const int MaxQueue = 10_000;

    /// <summary>How long <c>shutdown_pool</c> waits (<c>wait_for_termination(1)</c>) before it kills a pool.</summary>
    public static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(1);

    readonly WebPushClient connection;
    readonly Func<long, CancellationToken, Task>? invalidSubscriptionHandler;
    readonly JobLog log;
    readonly Channel<(WebPushNotification Notification, long SubscriptionId)> deliveries;
    readonly Channel<long> invalidations = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource killed = new();
    readonly Task[] deliveryWorkers;
    readonly Task invalidationWorker;
    long completedDeliveries;
    long completedInvalidations;
    int shutDown;

    /// <param name="connection">The client every delivery is sent through.</param>
    /// <param name="invalidSubscriptionHandler">
    /// Called with a dead subscription's id, one at a time; <see cref="DestroySubscription"/> in the
    /// app. Null leaves dead subscriptions alone.
    /// </param>
    /// <param name="log">Where errors in a delivery or the handler are logged.</param>
    /// <param name="maxThreads">Deliveries at once.</param>
    /// <param name="maxQueue">Deliveries waiting.</param>
    public WebPushPool(
        WebPushClient connection,
        Func<long, CancellationToken, Task>? invalidSubscriptionHandler,
        JobLog log,
        int maxThreads = MaxThreads,
        int maxQueue = MaxQueue)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(log);
        this.connection = connection;
        this.invalidSubscriptionHandler = invalidSubscriptionHandler;
        this.log = log;
        deliveries = Channel.CreateBounded<(WebPushNotification, long)>(new BoundedChannelOptions(maxQueue)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
        });
        deliveryWorkers = [.. Enumerable.Range(0, maxThreads).Select(_ => Task.Run(DeliverQueuedAsync))];
        invalidationWorker = Task.Run(InvalidateQueuedAsync);
    }

    /// <summary>The delivery pool's <c>completed_task_count</c>.</summary>
    public long CompletedDeliveries => Interlocked.Read(ref completedDeliveries);

    /// <summary>The invalidation pool's <c>completed_task_count</c>.</summary>
    public long CompletedInvalidations => Interlocked.Read(ref completedInvalidations);

    /// <summary>
    /// <c>queue(payload, subscriptions)</c>: each subscription's notification is built here, on the
    /// caller's database session (the badge is counted now), then handed to the pool.
    /// </summary>
    public void Queue(SqliteSession session, PushPayload payload, IEnumerable<PushSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        foreach (var subscription in subscriptions)
        {
            DeliverLater(WebPushNotification.For(session, subscription, payload), subscription.Id);
        }
    }

    /// <summary>
    /// <c>deliver_later</c>: false when the pool is full or shut down, which Rails rescues
    /// (<c>Concurrent::RejectedExecutionError</c>) and drops silently.
    /// </summary>
    public bool DeliverLater(WebPushNotification notification, long subscriptionId) =>
        deliveries.Writer.TryWrite((notification, subscriptionId));

    /// <summary>
    /// The <c>invalid_subscription_handler</c> reference/config/initializers/web_push.rb configures:
    /// logs, then destroys the subscription if it's still there.
    /// </summary>
    public static Func<long, CancellationToken, Task> DestroySubscription(SqliteDatabase database, JobLog log) =>
        async (subscriptionId, cancellationToken) =>
        {
            log(JobLogLevel.Information, $"Destroying push subscription: {subscriptionId}", null);
            await database.WriteAsync(transaction =>
            {
                if (PushSubscriptions.Find(transaction.Session, subscriptionId) is not null)
                {
                    PushSubscriptions.Delete(transaction.Session, subscriptionId);
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
        };

    /// <summary>
    /// <c>shutdown</c>: stop taking deliveries, give each pool a second to finish what it has, and
    /// abandon the rest.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref shutDown, 1) == 1)
        {
            return;
        }
        deliveries.Writer.TryComplete();
        await ShutdownPoolAsync(Task.WhenAll(deliveryWorkers)).ConfigureAwait(false);
        invalidations.Writer.TryComplete();
        await ShutdownPoolAsync(invalidationWorker).ConfigureAwait(false);
        connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        killed.Dispose();
    }

    // `shutdown_pool`: `pool.kill unless pool.wait_for_termination(1)`.
    async Task ShutdownPoolAsync(Task workers)
    {
        if (await Task.WhenAny(workers, Task.Delay(TerminationWait)).ConfigureAwait(false) != workers)
        {
            await killed.CancelAsync().ConfigureAwait(false);
        }
    }

    async Task DeliverQueuedAsync()
    {
        try
        {
            await foreach (var (notification, subscriptionId) in deliveries.Reader.ReadAllAsync(killed.Token).ConfigureAwait(false))
            {
                try
                {
                    await DeliverAsync(notification, subscriptionId).ConfigureAwait(false);
                }
                catch (Exception e) when (!killed.IsCancellationRequested)
                {
                    log(JobLogLevel.Error, $"Error in WebPush::Pool.deliver: {RubyClass(e)} {e.Message}", e);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    Interlocked.Increment(ref completedDeliveries);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // `deliver`: an expired subscription or an OpenSSL error queues the subscription's destruction.
    async Task DeliverAsync(WebPushNotification notification, long subscriptionId)
    {
        try
        {
            await notification.DeliverAsync(connection, killed.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebPushExpiredSubscriptionException or WebPushOpenSslException)
        {
            if (invalidSubscriptionHandler is not null)
            {
                InvalidateSubscriptionLater(subscriptionId);
            }
        }
    }

    // `invalidate_subscription_later`: posting to a shut-down pool raises, which `deliver`'s
    // caller logs.
    void InvalidateSubscriptionLater(long subscriptionId)
    {
        if (!invalidations.Writer.TryWrite(subscriptionId))
        {
            throw new InvalidOperationException("Concurrent::RejectedExecutionError");
        }
    }

    async Task InvalidateQueuedAsync()
    {
        try
        {
            await foreach (var subscriptionId in invalidations.Reader.ReadAllAsync(killed.Token).ConfigureAwait(false))
            {
                try
                {
                    await invalidSubscriptionHandler!(subscriptionId, killed.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (!killed.IsCancellationRequested)
                {
                    log(JobLogLevel.Error, $"Error in WebPush::Pool.invalid_subscription_handler: {RubyClass(e)} {e.Message}", e);
                }
                finally
                {
                    Interlocked.Increment(ref completedInvalidations);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    static string RubyClass(Exception e) => e switch
    {
        WebPushResponseException response => response.RubyClass,
        WebPushOpenSslException openSsl => openSsl.RubyClass,
        WebPushArgumentException => "ArgumentError",
        _ => e.GetType().Name,
    };
}
