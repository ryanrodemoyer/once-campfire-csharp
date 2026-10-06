using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Web;
using Campfire.Web.Assets;
using Campfire.Web.Routing;

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
    public static WebApplication Build(ServerSettings settings, string secretKeyBase, SqliteDatabase database, AssetBundle assets, string[] args)
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

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(assets);
        builder.Services.AddSingleton(new KeyGenerator(secretKeyBase));

        var app = builder.Build();
        app.Run(Pipeline(settings, assets));
        return app;
    }

    // The middleware Rails puts in front of the routes, in its order: AssumeSSL and SSL,
    // ActionDispatch::Static, then the router (with ShowExceptions' public error pages).
    static RequestDelegate Pipeline(ServerSettings settings, AssetBundle assets)
    {
        var staticFiles = new StaticFiles(assets);
        var router = new Router(Routes.Table, new ErrorPages(status => assets.Files.GetValueOrDefault($"/{status}.html")));
        return async context =>
        {
            if (settings.Ssl)
            {
                RailsSsl.Apply(context);
            }

            if (!await staticFiles.TryServeAsync(context).ConfigureAwait(false))
            {
                await router.HandleAsync(context).ConfigureAwait(false);
            }
        };
    }
}
