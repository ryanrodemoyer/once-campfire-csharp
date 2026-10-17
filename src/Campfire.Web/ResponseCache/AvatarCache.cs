using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace Campfire.Web.ResponseCache;

/// <summary>Whose avatar is cached. A type of its own, so no other entry in the cache can collide with it.</summary>
public sealed record AvatarKey(long UserId);

/// <summary>What an avatar is, which decides how it is sent.</summary>
public enum AvatarKind
{
    /// <summary>The 512px WebP of an uploaded image (<c>avatar.variant(:square)</c>).</summary>
    Upload,

    /// <summary>A bot's stock icon.</summary>
    BotIcon,

    /// <summary>The person's initials, as SVG.</summary>
    Initials,
}

/// <summary>
/// One avatar as the database and the disk answered for it: the validator the user's row gives
/// (<see cref="Pipeline.CacheKeys.Record"/>) and the body with what <c>send_file</c> says about it.
/// </summary>
public sealed record CachedAvatar(string Etag, AvatarKind Kind, string FileName, string ContentType, byte[] Body)
{
    // IMemoryCache counts an entry as its body plus a flat allowance for the rest.
    internal long Size => Body.Length + 256;
}

/// <summary>
/// The avatars <c>Users::AvatarsController</c> sends, kept for the 30 minutes it says
/// (<c>expires_in 30.minutes, public: true</c>), so a repeat request needs no query and no file read.
/// In the reference's image Thruster does this for any public response, within its own limits: 64 MB
/// all told and 1 MB an item (<see cref="MaxBytes"/> and <see cref="MaxItemBytes"/> are the same). The app
/// keeps its own copy for when Thruster isn't in front (<c>CAMPFIRE_THRUSTER=0</c>).
/// </summary>
public sealed class AvatarCache : IDisposable
{
    /// <summary>How long an avatar is kept: <c>expires_in 30.minutes</c>, and Thruster's time to live for it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Thruster's <c>CACHE_SIZE</c> default.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Thruster's <c>MAX_CACHE_ITEM_SIZE</c> default: a bigger avatar is sent but not kept.</summary>
    public const int MaxItemBytes = 1024 * 1024;

    readonly MemoryCache cache;
    long generation;

    public AvatarCache(TimeProvider? clock = null)
    {
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxBytes, Clock = clock is null ? null : new TimeProviderClock(clock) });
    }

    /// <summary>
    /// Counts the evictions so far. Read it before loading an avatar and hand it to <see cref="Add"/>: an avatar
    /// loaded while another was being evicted may be the very one that was just replaced.
    /// </summary>
    public long Generation => Volatile.Read(ref generation);

    public bool TryGet(long userId, out CachedAvatar avatar)
    {
        if (cache.TryGetValue(new AvatarKey(userId), out CachedAvatar? found) && found is not null)
        {
            avatar = found;
            return true;
        }
        avatar = null!;
        return false;
    }

    /// <summary>
    /// Keeps <paramref name="avatar"/> for <see cref="Lifetime"/>, unless it is too big or an eviction has
    /// happened since <paramref name="loadedAt"/> (the <see cref="Generation"/> before it was loaded).
    /// </summary>
    public void Add(long userId, CachedAvatar avatar, long loadedAt)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        if (avatar.Body.Length > MaxItemBytes || loadedAt != Generation)
        {
            return;
        }
        cache.Set(new AvatarKey(userId), avatar, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = avatar.Size });
    }

    /// <summary>
    /// Forgets the person's avatar, for when they or their avatar change, so the next request reads the new one.
    /// (Thruster would keep serving the old one until its 30 minutes were up; the profile page says as much.)
    /// </summary>
    public void Evict(long userId)
    {
        Interlocked.Increment(ref generation);
        cache.Remove(new AvatarKey(userId));
    }

    public void Dispose() => cache.Dispose();
}

// MemoryCache takes its clock as an ISystemClock; this is the app's TimeProvider behind it.
sealed class TimeProviderClock(TimeProvider time) : ISystemClock
{
    public DateTimeOffset UtcNow => time.GetUtcNow();
}
