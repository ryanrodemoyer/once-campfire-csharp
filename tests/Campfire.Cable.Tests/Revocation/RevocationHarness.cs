using Campfire.Cable.Channels;
using Campfire.Cable.Revocation;
using Campfire.Cable.Server;
using Campfire.Cable.Tests.Channels;
using Campfire.Cable.Tests.Server;
using Campfire.Data.Events;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;
using Microsoft.Data.Sqlite;

namespace Campfire.Cable.Tests.Revocation;

/// <summary>
/// Every Campfire channel behind the revocation guard, wired the way the app wires it: the
/// domain seams disconnect through <see cref="CableConnectionRevoker"/>.
/// </summary>
sealed class RevocationHarness : IAsyncDisposable
{
    /// <summary>Sessions come from a public address, so a ban can record it.</summary>
    public const string IpAddress = "198.51.100.7";

    readonly string directory;

    RevocationHarness(string directory, SqliteDatabase database, KeyGenerator keys, RevocationGuard guard, CableTestApp<User> app)
    {
        this.directory = directory;
        Database = database;
        Keys = keys;
        Guard = guard;
        App = app;
        Seams = new DomainSeams(app.Server, new NullJobs(), guard.RevokerFor(app.Server));
    }

    public SqliteDatabase Database { get; }

    public KeyGenerator Keys { get; }

    public RevocationGuard Guard { get; }

    public CableTestApp<User> App { get; }

    public CableServer<User> Server => App.Server;

    public DomainSeams Seams { get; }

    public static DateTimeOffset Now => ChannelHarness.Start;

    public static async Task<RevocationHarness> OpenAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "campfire-cable-revocation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(directory, "production.sqlite3")) { Readers = 2 });
        var keys = new KeyGenerator(RailsCompatVectors.File.SecretKeyBase);
        var guard = new RevocationGuard(database);
        var server = AppChannels.Register(
            CableServer.Builder(new CableConfig { AssumeSsl = false }, guard.Authenticate(new SessionCookieAuthenticator(database, keys)))
                .Clock(new ChannelHarness.ManualClock(ChannelHarness.Start)),
            database,
            keys).Build();
        var app = await CableTestApp.StartAsync(server);
        return new RevocationHarness(directory, database, keys, guard, app);
    }

    public async Task<(User User, string Cookie)> SignInAsync(string name, string email)
    {
        var user = await Database.WriteAsync(tx => Users.Create(tx.Session, name, email, null, Now), TestContext.Current.CancellationToken);
        var session = await Database.WriteAsync(
            tx => Sessions.Start(tx.Session, user.Id, "test", IpAddress, Now),
            TestContext.Current.CancellationToken);
        var jar = new CookieJar(Keys);
        jar.SetAuthenticationCookie(session.Token);
        var cookie = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));
        return (user, cookie);
    }

    public Task<Room> RoomAsync(RoomType type, string? name, long creatorId, params IReadOnlyList<long> memberIds) =>
        Database.WriteAsync(
            tx => Rooms.CreateFor(tx.Session, type, name, creatorId, memberIds, Now),
            TestContext.Current.CancellationToken);

    /// <summary>A welcomed connection for <paramref name="cookie"/>.</summary>
    public async Task<CableClient> ConnectAsync(string cookie)
    {
        var client = await App.ConnectAsync(cookie);
        Assert.Equal(CableProtocol.Welcome(), await client.NextAsync());
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    sealed class NullJobs : IJobQueue
    {
        public void Enqueue(Job job) => ArgumentNullException.ThrowIfNull(job);
    }
}
