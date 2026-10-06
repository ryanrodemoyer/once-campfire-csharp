using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Campfire.Data.Sqlite;
using Campfire.Server.Cli;
using Campfire.Server.Tests.Cli;
using Microsoft.AspNetCore.Builder;

namespace Campfire.Server.Tests.Front;

// Thruster in front of the app, as the image's bin/boot runs it (reference/Procfile: `thrust
// bin/start-app`). The proxy tests need the binary: CAMPFIRE_THRUST=<path to thrust>, which
// `src/Campfire.Server/Front/install-thruster amd64 <path>` installs.
public sealed partial class ThrusterTests : IAsyncLifetime
{
    static readonly string Front = Path.Combine(TestRoot.RepositoryRoot, "src", "Campfire.Server", "Front");
    static readonly string ReferencePublic = Path.Combine(TestRoot.RepositoryRoot, "reference", "public");

    readonly TestRoot root = new();
    readonly List<Process> thrusters = [];
    SqliteDatabase? database;
    WebApplication? app;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var thruster in thrusters)
        {
            thruster.Kill(entireProcessTree: true);
            await thruster.WaitForExitAsync();
            thruster.Dispose();
        }
        if (app is not null)
        {
            await app.DisposeAsync();
        }
        database?.Dispose();
        root.Dispose();
    }

    [Fact]
    public void Pins_the_gem_the_reference_bundles()
    {
        var gemfileLock = File.ReadAllText(Path.Combine(TestRoot.RepositoryRoot, "reference", "Gemfile.lock"));
        var bundled = LinuxThrusterGem().Matches(gemfileLock)
            .Select(match => $"thruster-{match.Groups[1].Value}.gem")
            .Order();

        var pinned = File.ReadAllLines(Path.Combine(Front, "thruster.sha256"))
            .Select(line => line.Split("  "))
            .ToList();

        Assert.Equal(["thruster-0.1.23-aarch64-linux.gem", "thruster-0.1.23-x86_64-linux.gem"], bundled);
        Assert.Equal(bundled, pinned.Select(entry => entry[1]).Order());
        Assert.All(pinned, entry => Assert.Matches("^[0-9a-f]{64}$", entry[0]));
    }

    [Fact]
    public async Task Proxies_to_the_app()
    {
        var targetPort = await BootApp();
        using var client = Client(StartThruster(targetPort));
        // The largest JavaScript file, well over gzhttp's 1 KB minimum.
        var script = TestRoot.Assets.Files.Where(file => file.Key.EndsWith(".js", StringComparison.Ordinal)).MaxBy(file => file.Value.Length).Key;

        using var robots = await client.GetAsync(new Uri("/robots.txt", UriKind.Relative), Cancellation);
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        Assert.Equal(["max-age=63072000; includeSubDomains"], robots.Headers.GetValues("Strict-Transport-Security"));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(ReferencePublic, "robots.txt"), Cancellation), await robots.Content.ReadAsByteArrayAsync(Cancellation));

        using var missing = await client.GetAsync(new Uri("/nope", UriKind.Relative), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(ReferencePublic, "404.html"), Cancellation), await missing.Content.ReadAsByteArrayAsync(Cancellation));

        // Thruster gzips what the client accepts; the bytes underneath are the app's.
        using var plain = await client.GetAsync(new Uri(script, UriKind.Relative), Cancellation);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal(TestRoot.Assets.Files[script], await plain.Content.ReadAsByteArrayAsync(Cancellation));
        using var request = new HttpRequestMessage(HttpMethod.Get, script);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var asset = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal(["gzip"], asset.Content.Headers.ContentEncoding);
        await using var gzip = new GZipStream(await asset.Content.ReadAsStreamAsync(Cancellation), CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await gzip.CopyToAsync(decompressed, Cancellation);
        Assert.Equal(TestRoot.Assets.Files[script], decompressed.ToArray());
    }

    [Fact]
    public async Task Serves_the_bad_gateway_page_while_the_app_is_down()
    {
        RequireThruster();
        using var client = Client(StartThruster(FreePort()));

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Cancellation);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(ReferencePublic, "502.html"), Cancellation), await response.Content.ReadAsByteArrayAsync(Cancellation));
    }

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    static string RequireThruster()
    {
        var thrust = Environment.GetEnvironmentVariable("CAMPFIRE_THRUST");
        Assert.SkipWhen(string.IsNullOrEmpty(thrust), "set CAMPFIRE_THRUST to a thrust binary (src/Campfire.Server/Front/install-thruster)");
        return thrust;
    }

    async Task<int> BootApp()
    {
        RequireThruster();
        root.CopySeed();
        var settings = root.Settings(("SECRET_KEY_BASE", "secret"));
        var port = FreePort();
        database = Commands.PrepareDatabase(settings);
        app = ServerCommand.Build(settings, SecretKeyBase.Resolve(settings), database, TestRoot.Assets, ["--urls", $"http://127.0.0.1:{port}"]);
        await app.StartAsync(Cancellation);
        return port;
    }

    // Thruster on a free HTTP port, proxying to targetPort, with the app's 502 page. The app runs
    // in this process, so Thruster's own upstream is a placeholder.
    int StartThruster(int targetPort)
    {
        var httpPort = FreePort();
        var start = new ProcessStartInfo(RequireThruster(), ["sleep", "600"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["HTTP_PORT"] = httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TARGET_PORT"] = targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["STORAGE_PATH"] = Path.Combine(root.Path, "storage", "thruster"),
                ["BAD_GATEWAY_PAGE"] = Path.Combine(ReferencePublic, "502.html"),
                ["LOG_REQUESTS"] = "false",
            },
        };
        var thruster = Process.Start(start)!;
        thrusters.Add(thruster);
        thruster.BeginOutputReadLine();
        thruster.BeginErrorReadLine();
        WaitForListener(httpPort);
        return httpPort;
    }

    static HttpClient Client(int port) =>
        new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        };

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static void WaitForListener(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                using var socket = new TcpClient();
                socket.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    [GeneratedRegex(@"^    thruster \((\S+-(?:x86_64|aarch64)-linux)\)$", RegexOptions.Multiline)]
    private static partial Regex LinuxThrusterGem();
}
