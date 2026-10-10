using Campfire.Data.Events;
using Campfire.Data.Sqlite;
using Campfire.Server.Composition;
using Campfire.Web.Assets;

namespace Campfire.Server.Cli;

/// <summary>
/// <c>campfire server</c>: the reference's <c>bin/start-app</c> (<c>db:prepare</c>, then Puma on
/// <c>PORT</c>, 3000 by default, on every interface). Thruster's public listener in front of it is
/// P03's.
/// </summary>
public static class ServerCommand
{
    public static async Task RunAsync(ServerSettings settings, string[] args)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var secretKeyBase = SecretKeyBase.Resolve(settings);
        var assets = LoadAssets(settings);
        var database = Commands.PrepareDatabase(settings, settings.MaxThreads);
        try
        {
            await using var app = Build(settings, secretKeyBase, database, assets, args);
            await app.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            Commands.Close(database);
        }
    }

    static AssetBundle LoadAssets(ServerSettings settings) =>
        Directory.Exists(Path.Combine(settings.AssetsDirectory, "public"))
            ? AssetBundle.Load(settings.AssetsDirectory)
            : throw new SettingsException($"No assets in {settings.AssetsDirectory}: run bin/build-assets, or set CAMPFIRE_ASSETS_PATH");

    /// <summary>
    /// The app over an open database and asset bundle. Kestrel options on the command line or in
    /// <c>ASPNETCORE_URLS</c> override the <c>PORT</c> listener.
    /// </summary>
    public static WebApplication Build(
        ServerSettings settings,
        string secretKeyBase,
        SqliteDatabase database,
        AssetBundle assets,
        string[] args,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assets);
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Puma sends no Server header and takes bodies of any size.
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;
        });
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{settings.Port}");
        }

        var composition = ServerComposition.Compose(settings, secretKeyBase, database, assets, clock);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(assets);
        builder.Services.AddSingleton(composition.Keys);
        builder.Services.AddSingleton(composition.Storage);
        builder.Services.AddSingleton(composition.Seams);
        builder.Services.AddSingleton(composition.CableServer);
        builder.Services.AddSingleton(composition.JobRunner);
        builder.Services.AddSingleton(composition.WebApp);
        builder.Services.AddSingleton<IJobQueue>(composition.JobRunner);
        builder.Services.AddSingleton<IBroadcaster>(composition.CableServer);
        builder.Services.AddSingleton<IConnectionRevoker>(composition.Revoker);
        builder.Services.AddSingleton(composition);
        builder.Services.AddHostedService(_ => new CompositionShutdownService(composition));

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(composition.Pipeline);
        return app;
    }

    sealed class CompositionShutdownService(ServerComposition composition) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken cancellationToken) => await composition.DisposeAsync().ConfigureAwait(false);
    }
}
