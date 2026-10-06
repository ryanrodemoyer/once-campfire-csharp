namespace Campfire.Data.Events;

// In-memory fakes of all three seams, recording into one log so a test sees their order: the
// lanes that raise domain events (messages, rooms, accounts) test against it without the cable
// server or the job runner.
public sealed class RecordingSeams : IBroadcaster, IJobQueue, IConnectionRevoker
{
    readonly List<SeamEvent> events = [];
    readonly Lock gate = new();

    public RecordingSeams() => Seams = new DomainSeams(this, this, this);

    public DomainSeams Seams { get; }

    public IReadOnlyList<SeamEvent> Events
    {
        get
        {
            lock (gate)
            {
                return [.. events];
            }
        }
    }

    public IReadOnlyList<Broadcast> Broadcasts => [.. Events.OfType<Broadcast>()];

    public IReadOnlyList<Job> Jobs => [.. Events.OfType<Enqueued>().Select(enqueued => enqueued.Job)];

    public IReadOnlyList<Disconnect> Disconnects => [.. Events.OfType<Disconnect>()];

    public void Clear()
    {
        lock (gate)
        {
            events.Clear();
        }
    }

    public void Broadcast(string stream, string payload) => Add(new Broadcast(stream, payload));

    public void Enqueue(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Add(new Enqueued(job));
    }

    public void Disconnect(long userId, bool reconnect) => Add(new Disconnect(userId, reconnect));

    void Add(SeamEvent seamEvent)
    {
        lock (gate)
        {
            events.Add(seamEvent);
        }
    }
}

public abstract record SeamEvent;

public sealed record Broadcast(string Stream, string Payload) : SeamEvent;

public sealed record Enqueued(Job Job) : SeamEvent;

public sealed record Disconnect(long UserId, bool Reconnect) : SeamEvent;
