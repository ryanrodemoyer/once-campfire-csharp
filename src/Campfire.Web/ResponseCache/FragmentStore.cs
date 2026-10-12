using System.Text;

namespace Campfire.Web.ResponseCache;

/// <summary>
/// The bounded fragment store (reference/app/models/fragment_cache.rb and
/// <c>ActiveSupport::Cache::MemoryStore</c>): one byte budget, FIFO eviction, and an entry
/// bigger than the budget is not kept. It does not replace the rate-limit counters.
/// </summary>
public sealed class FragmentStore
{
    readonly object gate = new();
    readonly Dictionary<string, (byte[] Bytes, int Size)> entries = [];
    readonly Queue<string> order = new();
    readonly long maxBytes;
    long usedBytes;
    int reads;
    int writes;

    static readonly AsyncLocal<bool> TransactionOpenLocal = new();

    public FragmentStore(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        this.maxBytes = maxBytes;
    }

    /// <summary>The production budget: 64 mebibytes.</summary>
    public const long DefaultBytes = 64L * 1024 * 1024;

    /// <summary>
    /// <c>FragmentCache.transaction_open?</c>. A request's reads do not open a transaction;
    /// tests set this so a render can see one the way <c>ActiveRecord::Base.connection</c> would.
    /// </summary>
    public static bool TransactionOpen
    {
        get => TransactionOpenLocal.Value;
        set => TransactionOpenLocal.Value = value;
    }

    public int Reads => Volatile.Read(ref reads);

    public int Writes => Volatile.Read(ref writes);

    /// <summary>When set, hits and collection renders are recorded for tests.</summary>
    public bool Track { get; set; }

    public List<string> HitKeys { get; } = [];

    public List<(string Identifier, int Count, int Hits)> Collections { get; } = [];

    public int ActionRenders { get; set; }

    /// <summary>Runs after a hit, before the value is returned. The record-cache test commits here.</summary>
    public Action? OnReadHit { get; set; }

    public byte[]? Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Interlocked.Increment(ref reads);
        byte[]? bytes;
        lock (gate)
        {
            bytes = entries.TryGetValue(key, out var entry) ? entry.Bytes : null;
        }
        if (bytes is not null)
        {
            OnReadHit?.Invoke();
        }
        return bytes;
    }

    public void Write(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Interlocked.Increment(ref writes);
        var size = Encoding.UTF8.GetByteCount(key) + value.Length;
        lock (gate)
        {
            if (size > maxBytes)
            {
                return;
            }
            if (entries.Remove(key, out var previous))
            {
                usedBytes -= previous.Size;
            }
            while (order.Count > 0 && usedBytes + size > maxBytes)
            {
                var oldest = order.Dequeue();
                if (entries.Remove(oldest, out var removed))
                {
                    usedBytes -= removed.Size;
                }
            }
            entries[key] = (value, size);
            order.Enqueue(key);
            usedBytes += size;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            order.Clear();
            usedBytes = 0;
        }
    }

    public void ClearStats()
    {
        HitKeys.Clear();
        Collections.Clear();
        ActionRenders = 0;
    }
}
