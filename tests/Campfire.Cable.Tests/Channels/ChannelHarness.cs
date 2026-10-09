using Campfire.Cable.Channels;
using Campfire.Cable.Server;
using Campfire.Cable.Tests.Server;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;
using Microsoft.Data.Sqlite;

namespace Campfire.Cable.Tests.Channels;

/// <summary>A database and a cable server with every Campfire channel, on a free local port.</summary>
sealed class ChannelHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 3, 2, 16, 0, 0, TimeSpan.Zero);

    readonly string directory;

    ChannelHarness(string directory, SqliteDatabase database, ManualClock clock, KeyGenerator keys, CableTestApp<User> app)
    {
        this.directory = directory;
        Database = database;
        Clock = clock;
        Keys = keys;
        App = app;
    }

    public SqliteDatabase Database { get; }

    public ManualClock Clock { get; }

    public KeyGenerator Keys { get; }

    public CableTestApp<User> App { get; }

    public static async Task<ChannelHarness> OpenAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "campfire-cable-channels", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var clock = new ManualClock(Start);
        var database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(directory, "production.sqlite3")) { Readers = 1 });
        var keys = new KeyGenerator(RailsCompatVectors.File.SecretKeyBase);
        var server = AppChannels.Register(
            CableServer.Builder(new CableConfig { AssumeSsl = false }, new SessionCookieAuthenticator(database, keys)).Clock(clock),
            database,
            keys).Build();
        var app = await CableTestApp.StartAsync(server);
        return new ChannelHarness(directory, database, clock, keys, app);
    }

    public async Task<(User User, string Cookie)> SignInAsync(string name, string email)
    {
        var user = await Database.WriteAsync(tx => Users.Create(tx.Session, name, email, null, Clock.UtcNow), TestContext.Current.CancellationToken);
        var session = await Database.WriteAsync(
            tx => Sessions.Start(tx.Session, user.Id, "test", "127.0.0.1", Clock.UtcNow),
            TestContext.Current.CancellationToken);
        var jar = new CookieJar(Keys);
        jar.SetAuthenticationCookie(session.Token);
        var cookie = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));
        return (user, cookie);
    }

    public Task<Room> RoomAsync(RoomType type, string? name, long creatorId, params IReadOnlyList<long> memberIds) =>
        Database.WriteAsync(
            tx => Rooms.CreateFor(tx.Session, type, name, creatorId, memberIds, Clock.UtcNow),
            TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    public sealed class ManualClock : TimeProvider
    {
        public ManualClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
