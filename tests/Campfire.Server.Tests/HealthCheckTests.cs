using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Server.Tests;

public sealed class HealthCheckTests : IAsyncLifetime
{
    WebApplication app = null!;
    HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        app = Program.Build(["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task UpReturnsOk()
    {
        using var response = await client.GetAsync(new Uri("/up", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPathReturnsNotFound()
    {
        using var response = await client.GetAsync(new Uri("/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
