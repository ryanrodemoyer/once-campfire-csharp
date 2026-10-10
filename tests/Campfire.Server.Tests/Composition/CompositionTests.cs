using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Cable.Channels;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Jobs.Runner;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Signing;
using Campfire.Server.Cli;
using Campfire.Server.Composition;
using Campfire.Server.Tests.Cli;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Server.Tests.Composition;

[Collection("CompositionTests")]
public sealed class CompositionTests : IDisposable
{
    readonly TestRoot root = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        root.Dispose();
    }

    [Fact]
    public async Task ServerComposition_wires_all_seams_and_services()
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret")]);
        using var database = Commands.PrepareDatabase(settings);

        await using var composition = ServerComposition.Compose(settings, SecretKeyBase.Resolve(settings), database, TestRoot.Assets);

        Assert.NotNull(composition.Database);
        Assert.NotNull(composition.Keys);
        Assert.NotNull(composition.Storage);
        Assert.NotNull(composition.Assets);
        Assert.NotNull(composition.Router);
        Assert.NotNull(composition.CableServer);
        Assert.NotNull(composition.Revoker);
        Assert.NotNull(composition.JobRunner);
        Assert.NotNull(composition.WebApp);
        Assert.NotNull(composition.Pipeline);

        Assert.Same(composition.CableServer, composition.Seams.Broadcaster);
        Assert.Same(composition.JobRunner, composition.Seams.Jobs);
        Assert.Same(composition.Revoker, composition.Seams.Connections);
        Assert.Same(composition.Seams, composition.WebApp.Seams);

        ServerComposition.ValidateSeams(composition.Seams);
        ServerComposition.ValidateJobs(composition.JobRunner);
    }

    public enum SeamMember
    {
        Broadcaster,
        Jobs,
        Connections,
    }

    [Theory]
    [InlineData(SeamMember.Broadcaster)]
    [InlineData(SeamMember.Jobs)]
    [InlineData(SeamMember.Connections)]
    public async Task Composition_test_fails_if_any_DomainSeams_member_is_a_no_op(SeamMember member)
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret")]);
        using var database = Commands.PrepareDatabase(settings);
        await using var composition = ServerComposition.Compose(settings, SecretKeyBase.Resolve(settings), database, TestRoot.Assets);

        var seams = member switch
        {
            SeamMember.Broadcaster => new DomainSeams(new NoOpBroadcaster(), composition.JobRunner, composition.Revoker),
            SeamMember.Jobs => new DomainSeams(composition.CableServer, new NoOpJobs(), composition.Revoker),
            SeamMember.Connections => new DomainSeams(composition.CableServer, composition.JobRunner, new NoOpRevoker()),
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

        var error = Assert.Throws<InvalidOperationException>(() => ServerComposition.ValidateSeams(seams));
        Assert.Contains(member.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(PushMessageJob))]
    [InlineData(typeof(WebhookJob))]
    [InlineData(typeof(RemoveBannedContentJob))]
    [InlineData(typeof(AnalyzeBlobJob))]
    [InlineData(typeof(PurgeBlobJob))]
    public void Composition_test_fails_if_any_job_type_enqueued_in_src_has_no_registered_handler(Type missingJobType)
    {
        var runner = new JobRunner((_, _, _) => { });
        var limits = new JobQueueLimits();

        if (missingJobType != typeof(PushMessageJob))
        {
            runner.Register<PushMessageJob>((_, _) => Task.CompletedTask, limits);
        }
        if (missingJobType != typeof(WebhookJob))
        {
            runner.Register<WebhookJob>((_, _) => Task.CompletedTask, limits);
        }
        if (missingJobType != typeof(RemoveBannedContentJob))
        {
            runner.Register<RemoveBannedContentJob>((_, _) => Task.CompletedTask, limits);
        }
        if (missingJobType != typeof(AnalyzeBlobJob))
        {
            runner.Register<AnalyzeBlobJob>((_, _) => Task.CompletedTask, limits);
        }
        if (missingJobType != typeof(PurgeBlobJob))
        {
            runner.Register<PurgeBlobJob>((_, _) => Task.CompletedTask, limits);
        }

        var error = Assert.Throws<InvalidOperationException>(() => ServerComposition.ValidateJobs(runner));
        Assert.Contains(missingJobType.Name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posted_message_to_room_with_bot_enqueues_and_runs_webhook_job()
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret")]);
        using var database = Commands.PrepareDatabase(settings);

        await using var webhookServer = new SimpleWebhookServer("Hello from Bender Bot!");
        var webhookUrl = webhookServer.Url;

        // In seeded fixtures, bender (user 394959859, role=2) is in direct room 340026324 with Kevin.
        // Update bender's webhook URL to point to our test listener.
        await database.WriteAsync(tx =>
        {
            tx.Session.Execute("UPDATE webhooks SET url = @url WHERE user_id = 394959859", ("@url", webhookUrl));
        }, TestContext.Current.CancellationToken);

        await using var composition = ServerComposition.Compose(settings, SecretKeyBase.Resolve(settings), database, TestRoot.Assets);

        // Find user Kevin and direct room with Bender
        var (kevin, room) = await database.ReadAsync(session =>
        {
            var user = Users.FindActiveByEmailAddress(session, "kevin@37signals.com") ?? Users.Find(session, 712064548)!;
            var r = Rooms.Find(session, 340026324)!;
            return (user, r);
        }, TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        var message = await database.WriteAsync(tx =>
        {
            return MessageLifecycle.Create(tx, composition.Seams, room.Id, kevin.Id, null, "<p>Status update?</p>", "Status update?", now);
        }, TestContext.Current.CancellationToken);

        await database.ReadAsync(session =>
        {
            MessageLifecycle.DeliverWebhooksToBots(session, composition.Seams.Jobs, room, message, []);
            return true;
        }, TestContext.Current.CancellationToken);

        // Wait for JobRunner to pick up and process WebhookJob
        var request = await webhookServer.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(request);
        Assert.Contains("Status update?", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serves_room_page_accepts_cable_connection_and_receives_broadcast()
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret"), ("DISABLE_SSL", "true")]);
        using var database = Commands.PrepareDatabase(settings);

        var keys = new KeyGenerator("secret");
        var (user, session, room) = await database.WriteAsync(tx =>
        {
            var u = Users.FindActiveByEmailAddress(tx.Session, "david@37signals.com") ?? Users.Find(tx.Session, 1)!;
            var s = Sessions.Start(tx.Session, u.Id, "test", "127.0.0.1", DateTimeOffset.UtcNow);
            var r = Rooms.Original(tx.Session)!;
            return (u, s, r);
        }, TestContext.Current.CancellationToken);

        var jar = new CookieJar(keys);
        jar.SetAuthenticationCookie(session.Token);
        var cookieHeader = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));

        await using var app = ServerCommand.Build(settings, "secret", database, TestRoot.Assets, ["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        var baseUri = new Uri(address);

        using var httpClient = new HttpClient { BaseAddress = baseUri };
        httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);

        // 1. Serves the room page
        using var roomResponse = await httpClient.GetAsync(new Uri($"/rooms/{room.Id}", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, roomResponse.StatusCode);
        var roomHtml = await roomResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(room.Name!, roomHtml, StringComparison.OrdinalIgnoreCase);

        // 2. Accepts a /cable connection
        var wsUri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/cable");
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("actioncable-v1-json");
        ws.Options.SetRequestHeader("Cookie", cookieHeader);
        ws.Options.SetRequestHeader("Origin", address);
        await ws.ConnectAsync(wsUri, TestContext.Current.CancellationToken);

        var welcomeFrame = await ReadWsMessageAsync(ws);
        Assert.NotNull(welcomeFrame);
        Assert.Equal("welcome", welcomeFrame["type"]?.GetValue<string>());

        // 3. Subscribe to RoomMessagesChannel (or Turbo stream for room messages)
        var gid = RoomSubscription.GidParam(room);
        var signedStreamName = TurboStreamName.SignedStreamName(keys, gid, "messages");
        var streamName = $"{gid}:messages";
        var subscribeIdentifier = new JsonObject
        {
            ["channel"] = "RoomMessagesChannel",
            ["signed_stream_name"] = signedStreamName,
        }.ToJsonString();

        var subscribeCommand = new JsonObject
        {
            ["command"] = "subscribe",
            ["identifier"] = subscribeIdentifier,
        }.ToJsonString();

        await ws.SendAsync(Encoding.UTF8.GetBytes(subscribeCommand), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        var confirmFrame = await ReadWsMessageAsync(ws);
        Assert.NotNull(confirmFrame);
        Assert.Equal("confirm_subscription", confirmFrame["type"]?.GetValue<string>());

        var seams = app.Services.GetRequiredService<DomainSeams>();
        seams.Broadcaster.Broadcast(streamName, "{\"message\":\"hello from test\"}");

        var messageFrame = await ReadWsMessageAsync(ws);
        Assert.NotNull(messageFrame);
        Assert.Equal(subscribeIdentifier, messageFrame["identifier"]?.GetValue<string>());
        Assert.Contains("hello from test", messageFrame["message"]?.ToString(), StringComparison.Ordinal);

        try
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
        }
        catch (WebSocketException)
        {
        }
        ws.Abort();
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Revoker_disconnects_active_cable_connection()
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret"), ("DISABLE_SSL", "true")]);
        using var database = Commands.PrepareDatabase(settings);

        var keys = new KeyGenerator("secret");
        var (user, session) = await database.WriteAsync(tx =>
        {
            var u = Users.FindActiveByEmailAddress(tx.Session, "david@37signals.com") ?? Users.Find(tx.Session, 1)!;
            var s = Sessions.Start(tx.Session, u.Id, "test", "127.0.0.1", DateTimeOffset.UtcNow);
            return (u, s);
        }, TestContext.Current.CancellationToken);

        var jar = new CookieJar(keys);
        jar.SetAuthenticationCookie(session.Token);
        var cookieHeader = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));

        await using var app = ServerCommand.Build(settings, "secret", database, TestRoot.Assets, ["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        var wsUri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/cable");

        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("actioncable-v1-json");
        ws.Options.SetRequestHeader("Cookie", cookieHeader);
        ws.Options.SetRequestHeader("Origin", address);
        await ws.ConnectAsync(wsUri, TestContext.Current.CancellationToken);

        _ = await ReadWsMessageAsync(ws); // welcome frame

        var seams = app.Services.GetRequiredService<DomainSeams>();
        seams.Connections.Disconnect(user.Id, reconnect: true);

        var disconnectFrame = await ReadWsMessageAsync(ws);
        Assert.NotNull(disconnectFrame);
        Assert.Equal("disconnect", disconnectFrame["type"]?.GetValue<string>());
        Assert.Equal("remote", disconnectFrame["reason"]?.GetValue<string>());
        Assert.True(disconnectFrame["reconnect"]?.GetValue<bool>());

        try
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
        }
        catch (WebSocketException)
        {
        }
        ws.Abort();
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Signing_out_sends_the_references_disconnect_to_the_users_cable_connections()
    {
        root.CopySeed();
        var settings = root.Settings([("SECRET_KEY_BASE", "secret"), ("DISABLE_SSL", "true")]);
        using var database = Commands.PrepareDatabase(settings);

        var keys = new KeyGenerator("secret");
        var (user, session, room) = await database.WriteAsync(tx =>
        {
            var u = Users.FindActiveByEmailAddress(tx.Session, "david@37signals.com") ?? Users.Find(tx.Session, 1)!;
            var s = Sessions.Start(tx.Session, u.Id, "test", "127.0.0.1", DateTimeOffset.UtcNow);
            var r = Rooms.Original(tx.Session)!;
            return (u, s, r);
        }, TestContext.Current.CancellationToken);

        var jar = new CookieJar(keys);
        jar.SetAuthenticationCookie(session.Token);
        var cookieHeader = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));

        await using var app = ServerCommand.Build(settings, "secret", database, TestRoot.Assets, ["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        var baseUri = new Uri(address);

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler) { BaseAddress = baseUri };
        httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);

        // 1. Fetch room page to get CSRF token
        using var roomResponse = await httpClient.GetAsync(new Uri($"/rooms/{room.Id}", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, roomResponse.StatusCode);
        var roomHtml = await roomResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var match = Regex.Match(roomHtml, @"<meta name=""csrf-token"" content=""([^""]+)""");
        Assert.True(match.Success);
        var csrfToken = match.Groups[1].Value;

        // 2. Connect client via /cable
        var wsUri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/cable");
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("actioncable-v1-json");
        ws.Options.SetRequestHeader("Cookie", cookieHeader);
        ws.Options.SetRequestHeader("Origin", address);
        await ws.ConnectAsync(wsUri, TestContext.Current.CancellationToken);

        _ = await ReadWsMessageAsync(ws); // welcome frame

        // 3. Sign out: DELETE /session with CSRF token and session cookie
        using var signOutRequest = new HttpRequestMessage(HttpMethod.Delete, new Uri("/session", UriKind.Relative));
        signOutRequest.Headers.Add("X-CSRF-Token", csrfToken);
        using var signOutResponse = await httpClient.SendAsync(signOutRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, signOutResponse.StatusCode);

        // 4. Remote disconnect delivered to the user's cable connection
        var disconnectFrame = await ReadWsMessageAsync(ws);
        Assert.NotNull(disconnectFrame);
        Assert.Equal("disconnect", disconnectFrame["type"]?.GetValue<string>());
        Assert.Equal("remote", disconnectFrame["reason"]?.GetValue<string>());
        Assert.True(disconnectFrame["reconnect"]?.GetValue<bool>());

        try
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
        }
        catch (WebSocketException)
        {
        }
        ws.Abort();
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ServerSettings_configures_job_concurrency()
    {
        var defaultSettings = root.Settings();
        Assert.Equal(ServerSettings.DefaultJobConcurrency(), defaultSettings.JobConcurrency);

        var customSettings = root.Settings(("JOB_CONCURRENCY", "4"));
        Assert.Equal(4, customSettings.JobConcurrency);

        var webConcurrencySettings = root.Settings(("WEB_CONCURRENCY", "6"));
        Assert.Equal(6, webConcurrencySettings.JobConcurrency);

        Assert.Throws<SettingsException>(() => root.Settings(("JOB_CONCURRENCY", "0")));
        Assert.Throws<SettingsException>(() => root.Settings(("JOB_CONCURRENCY", "-1")));
        Assert.Throws<SettingsException>(() => root.Settings(("JOB_CONCURRENCY", "invalid")));
    }

    static async Task<JsonNode?> ReadWsMessageAsync(ClientWebSocket ws, TimeSpan? timeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, timeoutCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }
            var json = Encoding.UTF8.GetString(stream.ToArray());
            var node = JsonNode.Parse(json);
            if (node is not null && string.Equals(node["type"]?.GetValue<string>(), "ping", StringComparison.Ordinal))
            {
                continue;
            }
            return node;
        }
    }

    sealed class NoOpBroadcaster : IBroadcaster
    {
        public void Broadcast(string stream, string payload) { }
    }

    sealed class NoOpJobs : IJobQueue
    {
        public void Enqueue(Job job) { }
    }

    sealed class NoOpRevoker : IConnectionRevoker
    {
        public void Disconnect(long userId, bool reconnect) { }
    }

    sealed class SimpleWebhookServer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource cts = new();
        readonly Task listenTask;
        readonly TaskCompletionSource<ReceivedRequest> requestTcs = new();
        readonly string replyText;

        public sealed record ReceivedRequest(string Method, string Path, string Body);

        public SimpleWebhookServer(string replyText)
        {
            this.replyText = replyText;
            listener.Start();
            listenTask = AcceptAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/webhook";

        public async Task<ReceivedRequest?> WaitForRequestAsync(TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                return await requestTcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        async Task AcceptAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) return;
                var parts = line.Split(' ');
                var method = parts[0];
                var path = parts.Length > 1 ? parts[1] : "/";

                int contentLength = 0;
                while (await reader.ReadLineAsync(cts.Token) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                var bodyChars = new char[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    var chunk = await reader.ReadAsync(bodyChars.AsMemory(read, contentLength - read), cts.Token);
                    if (chunk == 0) break;
                    read += chunk;
                }
                var body = new string(bodyChars, 0, read);

                requestTcs.TrySetResult(new ReceivedRequest(method, path, body));

                var replyBytes = Encoding.UTF8.GetBytes(replyText);
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {replyBytes.Length}\r\nConnection: close\r\n\r\n{replyText}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            listener.Stop();
            try { await listenTask; } catch { }
            cts.Dispose();
        }
    }
}
