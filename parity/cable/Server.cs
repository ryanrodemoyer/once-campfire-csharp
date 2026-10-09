#:project ../../src/Campfire.Web/Campfire.Web.csproj
#:project ../../src/Campfire.Data/Campfire.Data.csproj
#:project ../../src/Campfire.RailsCompat/Campfire.RailsCompat.csproj
#:project ../../src/Campfire.Storage/Campfire.Storage.csproj
#:project ../../src/Campfire.Cable/Campfire.Cable.csproj

using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Cable.Channels;
using Campfire.Cable.Revocation;
using Campfire.Cable.Server;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

var port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3200;
var dbPath = args.Length > 1 ? args[1] : "parity/.seed/default/db/production.sqlite3";
var queueCapacity = args.Length > 2
    ? int.Parse(args[2], CultureInfo.InvariantCulture)
    : (int.TryParse(Environment.GetEnvironmentVariable("CABLE_QUEUE_CAPACITY"), out var cap) ? cap : 100);

var secretKeyBase = Environment.GetEnvironmentVariable("SECRET_KEY_BASE")
    ?? "5335c3b1ad35b4ad170c3413bd651ef3b6ed64e257261871a6de3f978cf3868ee417a927040935fb30b0f7debdedb34a2a403e9f34b16cf594c917c2ecd4a995";

TimeProvider clock = TimeProvider.System;
var fakeTimeStr = Environment.GetEnvironmentVariable("FAKETIME") ?? Environment.GetEnvironmentVariable("PARITY_TIME");
if (!string.IsNullOrWhiteSpace(fakeTimeStr))
{
    var cleaned = fakeTimeStr.Trim().TrimStart('@');
    if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
    {
        clock = new OffsetClock(DateTimeOffset.UtcNow, parsed);
    }
}

var keys = new KeyGenerator(secretKeyBase);

// Prepare database directory
var isTempDb = !File.Exists(dbPath);
if (isTempDb)
{
    var dir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dir))
    {
        Directory.CreateDirectory(dir);
    }
}

var database = SqliteDatabase.Open(new SqliteDatabaseOptions(dbPath) { Readers = 2, Clock = clock });

// Seed standard records if new database
if (isTempDb)
{
    await database.WriteAsync(tx =>
    {
        var now = clock.GetUtcNow();
        var kevin = Users.Create(tx.Session, "Kevin", "kevin@example.com", null, now);
        var david = Users.Create(tx.Session, "David", "david@example.com", null, now);
        var openRoom = Rooms.CreateFor(tx.Session, RoomType.Open, "HQ", kevin.Id, [kevin.Id, david.Id], now);
        var closedRoom = Rooms.CreateFor(tx.Session, RoomType.Closed, "Secrets", kevin.Id, [kevin.Id], now);
        Sessions.Start(tx.Session, kevin.Id, "kevin_token", "127.0.0.1", now);
        Sessions.Start(tx.Session, david.Id, "david_token", "127.0.0.1", now);
    });
}

var guard = new RevocationGuard(database);
var cableConfig = new CableConfig
{
    AssumeSsl = false,
    QueueCapacity = queueCapacity,
};

var server = AppChannels.Register(
    CableServer.Builder(cableConfig, guard.Authenticate(new SessionCookieAuthenticator(database, keys)))
        .Clock(clock),
    database,
    keys).Build();

var seams = new DomainSeams(server, new NullJobs(), guard.RevokerFor(server));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

app.UseWebSockets();

app.MapGet("/up", async (HttpContext ctx) =>
{
    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsync("OK");
});

// Control endpoints for parity harness
app.MapPost("/_harness/signin", async (HttpContext context) =>
{
    var userQuery = context.Request.Query["user"].ToString();
    var email = userQuery.Contains('@', StringComparison.Ordinal) ? userQuery : $"{userQuery.ToLowerInvariant()}@example.com";
    var user = await database.ReadAsync(session => Users.FindActiveByEmailAddress(session, email));
    if (user is null)
    {
        user = await database.WriteAsync(tx => Users.Create(tx.Session, userQuery, email, null, clock.GetUtcNow()));
    }
    var sessionRecord = await database.WriteAsync(tx => Sessions.Start(tx.Session, user.Id, Guid.NewGuid().ToString("N"), "127.0.0.1", clock.GetUtcNow()));
    var jar = new CookieJar(keys);
    jar.SetAuthenticationCookie(sessionRecord.Token);
    foreach (var header in jar.ToSetCookieHeaders())
    {
        context.Response.Headers.Append("Set-Cookie", header);
    }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"user_id":{{user.Id}},"token":"{{sessionRecord.Token}}"}""");
});

app.MapPost("/_harness/signed_stream", async (HttpContext context) =>
{
    var stream = context.Request.Query["stream"].ToString();
    if (string.IsNullOrEmpty(stream))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing stream param");
        return;
    }
    var signed = TurboStreamName.SignedStreamName(keys, stream);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"signed_stream_name":"{{signed}}"}""");
});

app.MapPost("/_harness/revoke", async (HttpContext context) =>
{
    var userIdStr = context.Request.Query["userId"].ToString();
    if (!long.TryParse(userIdStr, out var userId))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid userId");
        return;
    }
    seams.Connections.Disconnect(userId, reconnect: true);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"revoked":{{userId}},"reconnect":true}""");
});

app.MapPost("/_harness/deactivate", async (HttpContext context) =>
{
    var userIdStr = context.Request.Query["userId"].ToString();
    if (!long.TryParse(userIdStr, out var userId))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid userId");
        return;
    }
    await database.WriteAsync(tx =>
    {
        var user = Users.Find(tx.Session, userId);
        if (user is not null)
        {
            UserLifecycle.Deactivate(tx, seams, user, clock.GetUtcNow());
        }
    });
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"deactivated":{{userId}},"reconnect":false}""");
});

app.MapPost("/_harness/ban", async (HttpContext context) =>
{
    var userIdStr = context.Request.Query["userId"].ToString();
    if (!long.TryParse(userIdStr, out var userId))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid userId");
        return;
    }
    await database.WriteAsync(tx =>
    {
        var user = Users.Find(tx.Session, userId);
        if (user is not null)
        {
            UserLifecycle.Ban(tx, seams, user, clock.GetUtcNow());
        }
    });
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"banned":{{userId}},"reconnect":false}""");
});

app.MapPost("/_harness/broadcast", async (HttpContext context) =>
{
    var stream = context.Request.Query["stream"].ToString();
    using var reader = new StreamReader(context.Request.Body);
    var payload = await reader.ReadToEndAsync();
    server.Broadcast(stream, payload);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"broadcast":"{{stream}}"}""");
});

app.MapPost("/_harness/lag_flood", async (HttpContext context) =>
{
    var stream = context.Request.Query["stream"].ToString();
    var countStr = context.Request.Query["count"].ToString();
    var count = int.TryParse(countStr, out var c) ? c : 5000;
    for (var i = 0; i < count; i++)
    {
        server.Broadcast(stream, JsonValue.Create(i));
    }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($$"""{"flooded":"{{stream}}","count":{{count}}}""");
});

app.Map(CableProtocol.DefaultMountPath, (HttpContext context) => server.HandleAsync(context));

Console.WriteLine($"Campfire Cable candidate listening on port {port} (QueueCapacity={cableConfig.QueueCapacity})");
await app.RunAsync();

sealed class OffsetClock(DateTimeOffset startedAt, DateTimeOffset virtualStart) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => virtualStart + (DateTimeOffset.UtcNow - startedAt);
}

sealed class NullJobs : IJobQueue
{
    public void Enqueue(Job job) => ArgumentNullException.ThrowIfNull(job);
}
