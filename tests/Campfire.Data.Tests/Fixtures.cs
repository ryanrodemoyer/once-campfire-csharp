using System.Text;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests;

// reference/test/fixtures in rails-fixtures.sqlite3: a fixture's id is
// `Zlib.crc32(label) % (2**30 - 1)` (`ActiveRecord::FixtureSet.identify`).
static class Fixtures
{
    // The time the fixtures were loaded at (Oracle/generate.rb).
    public static readonly DateTimeOffset LoadedAt = new(2026, 3, 2, 16, 0, 0, TimeSpan.Zero);

    static readonly uint[] Crc32Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c;
    }).ToArray();

    public static long Id(string label)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(label))
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return (crc ^ 0xFFFFFFFFu) % ((1L << 30) - 1);
    }

    // A database with the fixtures loaded, for one test.
    public sealed class Database : IDisposable
    {
        readonly TestDatabase file = TestDatabase.FromRailsFixtures();

        public Database() => Db = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });

        public SqliteDatabase Db { get; }

        public T Write<T>(Func<SqliteSession, T> work) =>
            Db.WriteAsync(tx => work(tx.Session), TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public void Dispose()
        {
            Db.Dispose();
            file.Dispose();
        }
    }
}
