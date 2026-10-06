using Campfire.Cable.PubSub;

namespace Campfire.Cable.Tests.PubSub;

/// <summary>reference-rust/crates/cable/src/pubsub.rs's tests.</summary>
public sealed class PubSubHubTests
{
    sealed class Sink : IFrameSink
    {
        public List<Frame> Frames { get; } = [];

        public void Deliver(Frame frame) => Frames.Add(frame);
    }

    [Fact]
    public void Delivers_to_every_subscriber_and_cleans_up()
    {
        var hub = new PubSubHub();
        var (a, b) = (new Sink(), new Sink());
        var subscriptionA = hub.Subscribe("room", null, a);
        var subscriptionB = hub.Subscribe("room", null, b);
        Assert.Equal(2, hub.Broadcast("room", "1"));
        Assert.Equal("1", a.Frames.Single().Text);
        Assert.Equal("1", b.Frames.Single().Text);

        subscriptionA.Dispose();
        subscriptionA.Dispose();
        Assert.Equal(1, hub.StreamCount);
        subscriptionB.Dispose();
        Assert.Equal(0, hub.StreamCount);
        Assert.Equal(0, hub.Broadcast("room", "2"));
    }

    [Fact]
    public void Wraps_payloads_once_per_identifier()
    {
        var hub = new PubSubHub();
        const string identifier = "\"{\\\"channel\\\":\\\"RoomChannel\\\"}\"";
        var (a, b, other, raw) = (new Sink(), new Sink(), new Sink(), new Sink());
        hub.Subscribe("room", identifier, a);
        hub.Subscribe("room", identifier, b);
        var otherSubscription = hub.Subscribe("room", "\"other\"", other);
        var rawSubscription = hub.Subscribe("room", null, raw);
        Assert.Equal(4, hub.Broadcast("room", """{"id":1}"""));

        Assert.Equal("""{"identifier":"{\"channel\":\"RoomChannel\"}","message":{"id":1}}""", a.Frames.Single().Text);
        Assert.Same(a.Frames.Single(), b.Frames.Single()); // one frame, encoded once, shared by both
        Assert.Equal("""{"identifier":"other","message":{"id":1}}""", other.Frames.Single().Text);
        Assert.Equal("""{"id":1}""", raw.Frames.Single().Text);

        otherSubscription.Dispose();
        rawSubscription.Dispose();
        Assert.Equal(1, hub.StreamCount);
        Assert.Equal(2, hub.Broadcast("room", "2"));
    }

    [Fact]
    public void Broadcastings_are_separate()
    {
        var hub = new PubSubHub();
        var sink = new Sink();
        using var subscription = hub.Subscribe("a", null, sink);
        Assert.Equal(0, hub.Broadcast("b", "1"));
        Assert.Empty(sink.Frames);
        Assert.Equal("a", subscription.Broadcasting);
    }

    [Fact]
    public async Task Subscribing_and_unsubscribing_while_broadcasting_is_safe()
    {
        var hub = new PubSubHub();
        var sink = new Sink();
        using var steady = hub.Subscribe("room", null, new CountingSink());
        var churn = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                hub.Subscribe("room", i % 2 == 0 ? null : "\"x\"", new CountingSink()).Dispose();
            }
        }, TestContext.Current.CancellationToken);
        while (!churn.IsCompleted)
        {
            Assert.True(hub.Broadcast("room", "1") >= 1);
        }
        await churn;
        Assert.Equal(1, hub.StreamCount);
        Assert.Empty(sink.Frames);
    }

    sealed class CountingSink : IFrameSink
    {
        int count;

        public void Deliver(Frame frame) => Interlocked.Increment(ref count);
    }
}
