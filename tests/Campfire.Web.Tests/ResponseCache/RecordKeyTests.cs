using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Campfire.Web.ResponseCache;

namespace Campfire.Web.Tests.ResponseCache;

/// <summary>
/// The keys the record snapshots are stored under (<c>RecordCache.expand</c> in
/// reference/app/models/record_cache.rb): every signed-in request builds one for its session and one for its
/// membership. They are written in one buffer now; <see cref="Before"/> is how they were built, with a string for each step,
/// and a key that differed would be a miss.
/// </summary>
public class RecordKeyTests
{
    static readonly char[] Alphabet =
        ("abcXYZ019 /?=&<>\"'\\:;,.-_~%+\n\t" + (char)0xe9 + (char)0x65e5 + char.ConvertFromUtf32(0x1F600) + (char)0xD800).ToCharArray();

    static string Text(Random random, int maxLength)
    {
        var builder = new StringBuilder();
        for (var i = random.Next(maxLength + 1); i > 0; i--)
        {
            builder.Append(Alphabet[random.Next(Alphabet.Length)]);
        }
        return builder.ToString();
    }

    static CacheVersion Version(Random random) => new(
        random.Next(4) == 0 ? "" : "/rails/storage/db/" + Text(random, 30),
        random.Next(4) == 0 ? "" : Text(random, 20),
        random.Next(6) == 0 ? random.NextInt64() : random.Next(100_000));

    [Fact]
    public void A_session_key_is_the_same_text_as_before()
    {
        var random = new Random(20261030);
        for (var i = 0; i < 5_000; i++)
        {
            var version = Version(random);
            // Tokens are 24 characters; the odd long or odd-looking one must still come out alike (a long one leaves the stack buffer).
            var token = i % 3 == 0 ? Text(random, 600) : new string(Enumerable.Range(0, 24).Select(_ => (char)('a' + random.Next(26))).ToArray());
            Assert.Equal(Before.Expand(version, Before.SessionKey(token)), RecordSnapshots.ExpandSession(version, token));
        }
    }

    [Fact]
    public void A_membership_key_is_the_same_text_as_before()
    {
        var random = new Random(20261031);
        for (var i = 0; i < 5_000; i++)
        {
            var version = Version(random);
            var userId = i % 5 == 0 ? random.NextInt64(long.MinValue, long.MaxValue) : random.Next(1_000_000);
            var roomId = i % 7 == 0 ? random.NextInt64(long.MinValue, long.MaxValue) : random.Next(1_000_000);
            Assert.Equal(
                Before.Expand(version, string.Create(CultureInfo.InvariantCulture, $"membership/{userId}/{roomId}")),
                RecordSnapshots.ExpandMembership(version, userId, roomId));
        }
    }

    [Fact]
    public void A_session_key_is_one_string()
    {
        var version = new CacheVersion("/rails/storage/db/production.sqlite3", "campfire", 41_276);
        var token = "gk3Zq1pQe7oT4mLw9sVxYb2d";
        var key = RecordSnapshots.ExpandSession(version, token);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            key = RecordSnapshots.ExpandSession(version, token);
        }
        var each = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // The string (two bytes a character and its header) and nothing else, give or take a little.
        Assert.InRange(each, 0, (key.Length * 2) + 22 + 64);
    }

    /// <summary>The keys as they were built.</summary>
    static class Before
    {
        public static string SessionKey(string token) => "session/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        public static string Expand(CacheVersion version, string key) =>
            string.Create(CultureInfo.InvariantCulture, $"record-snapshot-v1\n{version.Database}\n{version.Namespace}\n{version.DataVersion}\n{key}");
    }
}
