using Campfire.Cable.Server;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;

namespace Campfire.Cable.Tests.Server;

/// <summary>
/// <c>ApplicationCable::Connection#connect</c> over a real database, with a <c>session_token</c>
/// cookie Rails signed (vectors/campfire_sessions.json), so a browser signed in under Rails stays
/// connected after the switch.
/// </summary>
public sealed class SessionCookieAuthenticatorTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 3, 2, 16, 0, 0, TimeSpan.Zero);

    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-cable-tests", Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;

    public SessionCookieAuthenticatorTests()
    {
        Directory.CreateDirectory(directory);
        database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(directory, "production.sqlite3")) { Readers = 1 });
    }

    public void Dispose()
    {
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    static SessionCookieCase Signed => CampfireVectors.SessionsFile.Sessions[0];

    async Task<User> SignInAsync()
    {
        var user = await database.WriteAsync(tx => UserLifecycle.Create(tx, "David", "david@37signals.com", null, Now), TestContext.Current.CancellationToken);
        // `user.sessions.start!`, holding the token the vector's cookie was signed over.
        await database.WriteAsync(tx =>
        {
            var session = Sessions.Start(tx.Session, user.Id, "golden", "127.0.0.1", Now);
            tx.Session.Execute("""UPDATE "sessions" SET "token" = @token WHERE "id" = @id""", ("@token", Signed.Token), ("@id", session.Id));
        }, TestContext.Current.CancellationToken);
        return user;
    }

    async Task<CableTestApp<User>> StartAsync() =>
        await CableTestApp.StartAsync(
            CableServer.Builder(new CableConfig { AssumeSsl = false }, new SessionCookieAuthenticator(database, new KeyGenerator(RailsCompatVectors.File.SecretKeyBase)))
                .Channel("HeartbeatChannel", () => new EmptyChannel<User>())
                .Build());

    [Fact]
    public async Task A_rails_signed_session_cookie_connects_as_its_user()
    {
        var user = await SignInAsync();
        await using var app = await StartAsync();
        using var client = await app.ConnectAsync(Signed.CookieHeader);
        Assert.Equal("""{"type":"welcome"}""", await client.NextAsync());

        // identified_by :current_user: remote disconnects address the user's GlobalID.
        Assert.Equal(1, app.Server.Disconnect($"gid://campfire/User/{user.Id}", reconnect: true));
        Assert.Equal("""{"type":"disconnect","reason":"remote","reconnect":true}""", await client.NextAsync());
    }

    [Fact]
    public async Task No_cookie_a_forged_cookie_or_a_deleted_session_is_unauthorized()
    {
        await SignInAsync();
        await using var app = await StartAsync();
        const string unauthorized = """{"type":"disconnect","reason":"unauthorized","reconnect":false}""";

        foreach (var cookie in new[] { null, CampfireVectors.SessionsFile.Forged.CookieHeader, "session_token=tampered--0000" })
        {
            using var client = await app.ConnectAsync(cookie);
            Assert.Equal(unauthorized, await client.NextAsync());
        }

        await database.WriteAsync(tx => tx.Session.Execute("""DELETE FROM "sessions" """), TestContext.Current.CancellationToken);
        using var signedOut = await app.ConnectAsync(Signed.CookieHeader);
        Assert.Equal(unauthorized, await signedOut.NextAsync());
    }
}
