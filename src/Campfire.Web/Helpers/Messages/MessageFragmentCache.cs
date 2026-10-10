using System.Collections.Concurrent;

namespace Campfire.Web.Helpers;

/// <summary>
/// An LRU-ish fragment cache for message partials, keyed by message ID and its updated_at.
/// On a hit the cached UTF-8 bytes are emitted directly; on a miss the block renders and its
/// output is stored. Thread-safe: reads and writes can happen on any thread.
/// </summary>
public sealed class MessageFragmentCache
{
    /// <summary>Maximum number of cached fragments before eviction.</summary>
    const int maxEntries = 4096;

    readonly ConcurrentDictionary<string, Entry> entries = new();
    long sequence;

    /// <summary>
    /// The bytes cached for <paramref name="messageId"/> at <paramref name="updatedAt"/>,
    /// or null on a miss. The key includes a template version so editing the partial busts it.
    /// </summary>
    public byte[]? Get(long messageId, DateTimeOffset updatedAt, int templateVersion)
    {
        var key = Key(messageId, updatedAt, templateVersion);
        if (entries.TryGetValue(key, out var entry))
        {
            entry.Touch(ref sequence);
            return entry.Bytes;
        }
        return null;
    }

    /// <summary>
    /// Stores <paramref name="bytes"/> for <paramref name="messageId"/> at
    /// <paramref name="updatedAt"/>, evicting the oldest entry when at capacity.
    /// </summary>
    public void Set(long messageId, DateTimeOffset updatedAt, int templateVersion, byte[] bytes)
    {
        var key = Key(messageId, updatedAt, templateVersion);
        if (entries.Count >= maxEntries)
        {
            EvictOldest();
        }
        var entry = new Entry(bytes);
        entry.Touch(ref sequence);
        entries[key] = entry;
    }

    /// <summary>Number of entries currently cached.</summary>
    public int Count => entries.Count;

    static string Key(long messageId, DateTimeOffset updatedAt, int templateVersion) =>
        $"{messageId}:{updatedAt.ToUnixTimeMilliseconds()}:v{templateVersion}";

    void EvictOldest()
    {
        string? oldest = null;
        long oldestSeq = long.MaxValue;
        foreach (var (key, entry) in entries)
        {
            var seq = Volatile.Read(ref entry.Sequence);
            if (seq < oldestSeq)
            {
                oldestSeq = seq;
                oldest = key;
            }
        }
        if (oldest is not null)
        {
            entries.TryRemove(oldest, out _);
        }
    }

    sealed class Entry(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public long Sequence;

        public void Touch(ref long counter) => Sequence = Interlocked.Increment(ref counter);
    }
}
