using System.Net;
using Campfire.Data.Sqlite;
using Campfire.Server.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Server.Tests.Cli;

// The server booted on a copy of the seed, as the image runs it.
public sealed class ServerTests : IAsyncLifetime
{
    readonly TestRoot root = new();
    SqliteDatabase database = null!;
    WebApplication app = null!;
    HttpClient client = null!;

    async Task Boot(params (string Name, string? Value)[] env)
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret"), .. env]);
        database = Commands.PrepareDatabase(settings);
        app = ServerCommand.Build(settings, SecretKeyBase.Resolve(settings), database, TestRoot.Assets, ["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
        database?.Dispose();
        root.Dispose();
    }

    Task<HttpResponseMessage> Get(string path) =>
        client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Serves_the_public_files_and_assets()
    {
        await Boot();
        var (_, digested) = TestRoot.Assets.Manifest[0];

        using var asset = await Get($"/assets/{digested}");
        using var robots = await Get("/robots.txt");

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=2592000", asset.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        Assert.Equal(
            await File.ReadAllBytesAsync(System.IO.Path.Combine(TestRoot.RepositoryRoot, "reference", "public", "robots.txt"), TestContext.Current.CancellationToken),
            await robots.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Routes_everything_else()
    {
        await Boot();

        using var unknown = await Get("/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(
            await File.ReadAllBytesAsync(System.IO.Path.Combine(TestRoot.RepositoryRoot, "reference", "public", "404.html"), TestContext.Current.CancellationToken),
            await unknown.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // /up (rails/health#show) and the login page (sessions#new) reach their routes; A08 and A01
        // bind their handlers.
        foreach (var path in new[] { "/up", "/session/new" })
        {
            using var routed = await Get(path);
            Assert.NotEqual(HttpStatusCode.NotFound, routed.StatusCode);
        }
    }

    [Fact]
    public async Task Every_response_is_hsts_and_carries_no_server_header()
    {
        await Boot();

        foreach (var path in new[] { "/robots.txt", "/nope", "/up" })
        {
            using var response = await Get(path);
            Assert.Equal(["max-age=63072000; includeSubDomains"], response.Headers.GetValues("Strict-Transport-Security"));
            Assert.False(response.Headers.Contains("Server"), path);
        }
    }

    [Fact]
    public async Task Disable_ssl_turns_off_hsts()
    {
        await Boot(("DISABLE_SSL", "true"));

        using var response = await Get("/robots.txt");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task The_seed_stays_readable_and_writable()
    {
        await Boot();

        Assert.Same(database, app.Services.GetRequiredService<SqliteDatabase>());
        var messages = await database.ReadAsync(session => session.Scalar<long>("SELECT COUNT(*) FROM messages"), TestContext.Current.CancellationToken);
        Assert.True(messages > 0);
    }
}
