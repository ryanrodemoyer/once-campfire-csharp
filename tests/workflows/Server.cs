#:project ../../src/Campfire.Web/Campfire.Web.csproj
#:project ../../src/Campfire.Data/Campfire.Data.csproj
#:project ../../src/Campfire.RailsCompat/Campfire.RailsCompat.csproj
#:project ../../src/Campfire.Storage/Campfire.Storage.csproj
#:project ../../src/Campfire.Cable/Campfire.Cable.csproj
#:project ../../src/Campfire.Jobs/Campfire.Jobs.csproj

using System.Globalization;
using Campfire.Cable.Channels;
using Campfire.Cable.Revocation;
using Campfire.Cable.Server;
using Campfire.Data.Events;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Blobs;
using Campfire.Web;
using Campfire.Web.Assets;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3200;
var dbPath = args.Length > 1 ? args[1] : "parity/.seed/default/db/production.sqlite3";
var storagePath = args.Length > 2 ? args[2] : "parity/.seed/default/storage";
var assetsPath = args.Length > 3 ? args[3] : "artifacts/assets";

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
var database = SqliteDatabase.Open(new SqliteDatabaseOptions(dbPath) { Readers = 4, Clock = clock });
var assets = AssetBundle.Load(assetsPath);
var router = new Router(Routes.Table, new ErrorPages(status => assets.Files.GetValueOrDefault($"/{status}.html")));
var storage = BlobStorage.Local(storagePath, keys);

var guard = new RevocationGuard(database);
var cableConfig = new CableConfig
{
    AssumeSsl = false,
    QueueCapacity = 500,
};

var cableServer = AppChannels.Register(
    CableServer.Builder(cableConfig, guard.Authenticate(new SessionCookieAuthenticator(database, keys)))
        .Clock(clock),
    database,
    keys).Build();

var seams = new DomainSeams(cableServer, new NullJobs(), guard.RevokerFor(cableServer));

var webApp = new WebApp
{
    Database = database,
    Keys = keys,
    Router = router,
    Assets = assets,
    Storage = storage,
    Seams = seams,
    Clock = clock,
    AssumeSsl = false,
};

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = null;
});

var app = builder.Build();
app.UseWebSockets();

var staticFiles = new StaticFiles(assets);

app.Run(async context =>
{
    if (context.Request.Path.StartsWithSegments(CableProtocol.DefaultMountPath))
    {
        await cableServer.HandleAsync(context).ConfigureAwait(false);
        return;
    }

    if (!await staticFiles.TryServeAsync(context).ConfigureAwait(false))
    {
        await webApp.HandleAsync(context).ConfigureAwait(false);
    }
});

Console.WriteLine($"Campfire C# candidate listening on port {port}");
await app.RunAsync().ConfigureAwait(false);

sealed class OffsetClock(DateTimeOffset startedAt, DateTimeOffset virtualStart) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => virtualStart + (DateTimeOffset.UtcNow - startedAt);
}

sealed class NullJobs : IJobQueue
{
    public void Enqueue(Job job) => ArgumentNullException.ThrowIfNull(job);
}
