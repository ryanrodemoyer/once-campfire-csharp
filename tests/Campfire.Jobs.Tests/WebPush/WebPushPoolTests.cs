using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.Tests.RestrictedHttp;
using Campfire.Jobs.WebPush;

namespace Campfire.Jobs.Tests.WebPush;

// WebPush::Pool (reference/lib/web_push/pool.rb): a bounded delivery pool that drops what it
// can't take, and a shutdown that waits a second before abandoning deliveries.
public sealed class WebPushPoolTests : IDisposable
{
    readonly RecordingPushHandler service = new();
    readonly List<string> logged = [];

    WebPushPool Pool(int maxThreads, int maxQueue) => new(
        new WebPushClient(service, new PrivateNetworkGuard(FakeResolver.Of(PushEndpointTests.PublicTestIp)),
            new VapidIdentification(ReferenceVector.VapidPublicKey, ReferenceVector.VapidPrivateKey), TimeProvider.System),
        invalidSubscriptionHandler: null,
        (level, message, _) =>
        {
            lock (logged)
            {
                logged.Add($"{level}: {message}");
            }
        },
        maxThreads,
        maxQueue);

    static WebPushNotification Notification => new("t", "b", "/", 0, ReferenceVector.Endpoint, ReferenceVector.P256dh, ReferenceVector.Auth);

    static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timeout waiting for the pool");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_full_pool_drops_deliveries_without_a_word()
    {
        var gate = new TaskCompletionSource();
        service.Gate = gate.Task;
        await using var pool = Pool(maxThreads: 1, maxQueue: 2);

        Assert.True(pool.DeliverLater(Notification, 1));
        await WaitFor(() => service.Requests.Count == 1);
        Assert.True(pool.DeliverLater(Notification, 2));
        Assert.True(pool.DeliverLater(Notification, 3));
        Assert.False(pool.DeliverLater(Notification, 4));

        gate.SetResult();
        await WaitFor(() => pool.CompletedDeliveries == 3);
        Assert.Equal(3, service.Requests.Count);
        Assert.Empty(logged);
    }

    [Fact]
    public async Task Shutdown_waits_a_second_then_abandons_deliveries()
    {
        service.Gate = new TaskCompletionSource().Task;
        var pool = Pool(maxThreads: 1, maxQueue: 10);
        pool.DeliverLater(Notification, 1);
        pool.DeliverLater(Notification, 2);
        await WaitFor(() => service.Requests.Count == 1);

        var started = DateTime.UtcNow;
        await pool.DisposeAsync();

        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
        Assert.Single(service.Requests);
        Assert.False(pool.DeliverLater(Notification, 3));
        Assert.Empty(logged);
    }

    [Fact]
    public async Task A_dead_subscription_is_left_alone_without_a_handler()
    {
        service.Status = System.Net.HttpStatusCode.Gone;
        await using var pool = Pool(maxThreads: 1, maxQueue: 10);

        pool.DeliverLater(Notification, 1);
        await WaitFor(() => pool.CompletedDeliveries == 1);
        await pool.ShutdownAsync();

        Assert.Equal(0, pool.CompletedInvalidations);
        Assert.Empty(logged);
    }

    public void Dispose() => service.Dispose();
}
