using System.Globalization;
using Campfire.RailsCompat.Signing;
using Campfire.Web.ResponseCache;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.Avatars;

/// <summary>
/// <see cref="AvatarCache"/>: what Thruster does for the reference's public, 30-minute avatars, kept
/// in the app. The replay in <see cref="AvatarsControllerTests"/> is what says every response is the
/// reference's; these say when one is served from memory.
/// </summary>
public sealed class AvatarCacheTests : IDisposable
{
    // A signed-in session of the parity seed (tests/Campfire.Data.Tests/Oracle).
    const string david = "AxJs94fteQ5Autv2VrKsH68c";

    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-02T16:00:00Z", CultureInfo.InvariantCulture);

    readonly MessagesApp app = new(Now);

    public void Dispose() => app.Dispose();

    // Someone whose avatar is their initials, so a new name shows in the SVG.
    long PersonWithInitials() => (long)app.Scalar(
        """
        SELECT id FROM users
        WHERE bot_token IS NULL
          AND id NOT IN (SELECT record_id FROM active_storage_attachments WHERE record_type = 'User' AND name = 'avatar')
        ORDER BY id LIMIT 1
        """)!;

    static async Task<Response> GetAvatar(MessagesApp from, long userId, string? ifNoneMatch = null)
    {
        var headers = from.SignedIn(david, accept: "*/*");
        if (ifNoneMatch is not null)
        {
            headers["If-None-Match"] = ifNoneMatch;
        }
        return await from.SendAsync("GET", $"/users/{TransferableUser.GenerateAvatarSignedId(from.Keys, userId)}/avatar", headers);
    }

    void Rename(long userId, string name)
    {
        using var connection = app.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE users SET name = '{name}' WHERE id = {userId}";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_repeat_request_is_answered_from_memory()
    {
        var person = PersonWithInitials();
        var first = await GetAvatar(app, person);
        Assert.Equal(200, first.Status);
        Assert.True(app.App.Avatars.TryGet(person, out _));

        // The database changes under it. Within its 30 minutes the avatar doesn't, as with Thruster.
        Rename(person, "Zed Zed");
        var second = await GetAvatar(app, person);

        Assert.Equal(200, second.Status);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(first.Headers["ETag"].ToString(), second.Headers["ETag"].ToString());
        Assert.DoesNotContain("ZZ", second.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cached_avatar_is_still_revalidated_on_every_request()
    {
        var person = PersonWithInitials();
        var first = await GetAvatar(app, person);

        var again = await GetAvatar(app, person, ifNoneMatch: first.Headers["ETag"].ToString());

        // As the reference answers it (avatars.json, "initials, cached"): the same ETag, no cache headers of its own.
        Assert.Equal(304, again.Status);
        Assert.Equal(first.Headers["ETag"].ToString(), again.Headers["ETag"].ToString());
        Assert.Equal("no-cache", again.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task A_304_on_a_miss_caches_nothing()
    {
        var person = PersonWithInitials();
        string etag;
        using (var other = new MessagesApp(Now))
        {
            etag = (await GetAvatar(other, person)).Headers["ETag"].ToString();
        }

        var revalidated = await GetAvatar(app, person, ifNoneMatch: etag);

        Assert.Equal(304, revalidated.Status);
        Assert.False(app.App.Avatars.TryGet(person, out _));
    }

    [Fact]
    public async Task Each_person_has_their_own_entry()
    {
        var person = PersonWithInitials();
        var other = (long)app.Scalar($"SELECT id FROM users WHERE bot_token IS NULL AND id <> {person} ORDER BY id LIMIT 1")!;

        await GetAvatar(app, person);

        Assert.True(app.App.Avatars.TryGet(person, out _));
        Assert.False(app.App.Avatars.TryGet(other, out _));
    }

    [Fact]
    public void An_avatar_is_kept_for_thirty_minutes()
    {
        var clock = new SteppingClock(Now);
        using var cache = new AvatarCache(clock);
        cache.Add(7, Avatar(100), cache.Generation);

        clock.Advance(AvatarCache.Lifetime - TimeSpan.FromSeconds(1));
        Assert.True(cache.TryGet(7, out _));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet(7, out _));
    }

    [Fact]
    public void An_avatar_over_one_megabyte_is_not_kept()
    {
        using var cache = new AvatarCache();
        cache.Add(1, Avatar(AvatarCache.MaxItemBytes), cache.Generation);
        cache.Add(2, Avatar(AvatarCache.MaxItemBytes + 1), cache.Generation);

        Assert.True(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
    }

    [Fact]
    public void Evicting_a_person_forgets_only_them()
    {
        using var cache = new AvatarCache();
        cache.Add(1, Avatar(100), cache.Generation);
        cache.Add(2, Avatar(100), cache.Generation);

        cache.Evict(1);

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
    }

    [Fact]
    public void An_avatar_loaded_before_an_eviction_is_not_kept()
    {
        using var cache = new AvatarCache();
        var loadedAt = cache.Generation;
        // Another request replaces the avatar while this one is still reading the old one.
        cache.Evict(1);

        cache.Add(1, Avatar(100), loadedAt);
        Assert.False(cache.TryGet(1, out _));

        cache.Add(1, Avatar(100), cache.Generation);
        Assert.True(cache.TryGet(1, out _));
    }

    [Fact]
    public async Task A_profile_update_replaces_the_cached_avatar()
    {
        var me = (long)app.Scalar($"SELECT user_id FROM sessions WHERE token = '{david}'")!;
        // An old updated_at, so the update below certainly moves the ETag (the clock here is fixed).
        using (var connection = app.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE users SET updated_at = '2020-01-01 00:00:00.000000' WHERE id = {me}";
            command.ExecuteNonQuery();
        }
        var before = await GetAvatar(app, me);
        Assert.True(app.App.Avatars.TryGet(me, out _));

        var update = await app.SendAsync("PATCH", "/users/me/profile", app.SignedIn(david), "user%5Bname%5D=Zed+Zed");
        Assert.Equal(302, update.Status);
        Assert.False(app.App.Avatars.TryGet(me, out _));

        var after = await GetAvatar(app, me);
        Assert.Equal(200, after.Status);
        Assert.NotEqual(before.Headers["ETag"].ToString(), after.Headers["ETag"].ToString());
        if (before.Body.StartsWith("<svg", StringComparison.Ordinal))
        {
            Assert.Contains("ZZ", after.Body, StringComparison.Ordinal);
        }
    }

    static CachedAvatar Avatar(int bytes) => new("W/\"etag\"", AvatarKind.Upload, "avatar.webp", "image/webp", new byte[bytes]);

    sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
