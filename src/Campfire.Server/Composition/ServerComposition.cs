using System.Globalization;
using Campfire.Cable.Channels;
using Campfire.Cable.Revocation;
using Campfire.Cable.Server;
using Campfire.Data.Events;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.Runner;
using Campfire.Jobs.Webhooks;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Crypto;
using Campfire.Server.Cli;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Web;
using Campfire.Web.Assets;
using Campfire.Web.Controllers;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Server.Composition;

/// <summary>
/// Composes the production Campfire server: WebApp with real Database, Keys, Storage, and Assets;
/// CableServer mounted at /cable with session-cookie authentication; JobRunner processing jobs
/// in-process sized by <c>JOB_CONCURRENCY</c>; connection revoker wired to the cable server;
/// and <see cref="DomainSeams"/> built from those three, never no-ops.
/// </summary>
public sealed partial class ServerComposition : IAsyncDisposable
{
    readonly bool ownsPushClient;
    readonly bool ownsPushPool;
    readonly bool ownsWebhookClient;

    public required ServerSettings Settings { get; init; }
    public required SqliteDatabase Database { get; init; }
    public required KeyGenerator Keys { get; init; }
    public required BlobStorage Storage { get; init; }
    public required AssetBundle Assets { get; init; }
    public required Router Router { get; init; }
    public required CableServer<User> CableServer { get; init; }
    public required RevocationGuard RevocationGuard { get; init; }
    public required CableConnectionRevoker Revoker { get; init; }
    public required JobRunner JobRunner { get; init; }
    public required DomainSeams Seams { get; init; }
    public required WebApp WebApp { get; init; }
    public required WebPushClient PushClient { get; init; }
    public required WebPushPool PushPool { get; init; }
    public required WebhookClient WebhookClient { get; init; }
    public required BotWebhooks BotWebhooks { get; init; }
    public required RemoveBannedContent RemoveBannedContent { get; init; }
    public required MessagePusher MessagePusher { get; init; }
    public required MessageAttachmentJobs AttachmentJobs { get; init; }
    public required TimeProvider Clock { get; init; }
    public required RequestDelegate Pipeline { get; init; }

    internal ServerComposition(bool ownsPushClient, bool ownsPushPool, bool ownsWebhookClient)
    {
        this.ownsPushClient = ownsPushClient;
        this.ownsPushPool = ownsPushPool;
        this.ownsWebhookClient = ownsWebhookClient;
    }

    int disposed;

    public static ServerComposition Compose(
        ServerSettings settings,
        string secretKeyBase,
        SqliteDatabase database,
        AssetBundle assets,
        TimeProvider? clock = null,
        ILogger? logger = null,
        WebhookClient? webhookClient = null,
        WebPushClient? pushClient = null,
        WebPushPool? pushPool = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secretKeyBase);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(assets);

        clock ??= ResolveClock();
        var log = logger ?? NullLogger.Instance;
        var keys = new KeyGenerator(secretKeyBase);
        var storage = BlobStorage.Local(settings.FilesPath, keys, Marcel.Identify);
        var router = new Router(Routes.Table, new ErrorPages(status => assets.Files.GetValueOrDefault($"/{status}.html")));

        var guard = new RevocationGuard(database);
        var cableConfig = new CableConfig
        {
            AssumeSsl = settings.Ssl,
            QueueCapacity = 500,
        };

        var cableServer = AppChannels.Register(
            Campfire.Cable.Server.CableServer.Builder(cableConfig, guard.Authenticate(new SessionCookieAuthenticator(database, keys)))
                .Clock(clock)
                .Logger(log),
            database,
            keys).Build();

        var revoker = guard.RevokerFor(cableServer);

        JobLog jobLog = (level, message, exception) =>
        {
            switch (level)
            {
                case JobLogLevel.Information:
                    LogJobInfo(log, message);
                    break;
                case JobLogLevel.Warning:
                    LogJobWarn(log, message, exception);
                    break;
                case JobLogLevel.Error:
                    LogJobError(log, message, exception);
                    break;
            }
        };

        var runner = new JobRunner(jobLog);
        var seams = new DomainSeams(cableServer, runner, revoker);
        ValidateSeams(seams);

        var ownsPushClient = pushClient is null;
        pushClient ??= new WebPushClient(
            RestrictedHttpHandlers.WebPush(PrivateNetworkGuard.System),
            PrivateNetworkGuard.System,
            new VapidIdentification(settings.VapidPublicKey, settings.VapidPrivateKey),
            clock);

        var ownsPushPool = pushPool is null;
        pushPool ??= new WebPushPool(
            pushClient,
            WebPushPool.DestroySubscription(database, jobLog),
            jobLog);

        var webApp = new WebApp
        {
            Database = database,
            Keys = keys,
            Router = router,
            Assets = assets,
            Storage = storage,
            Seams = seams,
            Clock = clock,
            AssumeSsl = settings.Ssl,
            AppVersion = settings.AppVersion,
            GitRevision = settings.GitRevision,
            VapidPublicKey = settings.VapidPublicKey,
            WebPush = pushClient,
            Logger = log,
            ResetRemoteConnections = user =>
            {
                revoker.Disconnect(user.Id, reconnect: true);
                return Task.CompletedTask;
            },
        };

        var limits = new JobQueueLimits(Capacity: JobQueueLimits.DefaultCapacity, Concurrency: settings.JobConcurrency);

        var ownsWebhookClient = webhookClient is null;
        webhookClient ??= new WebhookClient();

        var botWebhooks = new BotWebhooks(
            database,
            webhookClient,
            new ByBotsWebhookReplies(webApp),
            session => new DatabaseAttachables(session, keys, clock.GetUtcNow()));
        botWebhooks.RegisterWith(runner, limits);

        var removeBannedContent = new RemoveBannedContent(database, cableServer, clock);
        removeBannedContent.RegisterWith(runner, limits);

        var messagePusher = new MessagePusher(
            database,
            pushPool,
            session => new DatabaseAttachables(session, keys, clock.GetUtcNow()),
            clock);
        messagePusher.RegisterWith(runner, limits);

        var attachmentJobs = new MessageAttachmentJobs(database, storage, keys, clock);
        attachmentJobs.RegisterWith(runner, limits);
        ValidateJobs(runner);

        return new ServerComposition(ownsPushClient, ownsPushPool, ownsWebhookClient)
        {
            Settings = settings,
            Database = database,
            Keys = keys,
            Storage = storage,
            Assets = assets,
            Router = router,
            CableServer = cableServer,
            RevocationGuard = guard,
            Revoker = revoker,
            JobRunner = runner,
            Seams = seams,
            WebApp = webApp,
            PushClient = pushClient,
            PushPool = pushPool,
            WebhookClient = webhookClient,
            BotWebhooks = botWebhooks,
            RemoveBannedContent = removeBannedContent,
            MessagePusher = messagePusher,
            AttachmentJobs = attachmentJobs,
            Clock = clock,
            Pipeline = CreatePipeline(settings, assets, cableServer, webApp),
        };
    }

    public static RequestDelegate CreatePipeline(ServerSettings settings, AssetBundle assets, CableServer<User> cableServer, WebApp webApp)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(cableServer);
        ArgumentNullException.ThrowIfNull(webApp);

        var staticFiles = new StaticFiles(assets);
        return async context =>
        {
            if (settings.Ssl)
            {
                RailsSsl.Apply(context);
            }

            if (context.Request.Path.StartsWithSegments(CableProtocol.DefaultMountPath))
            {
                await cableServer.HandleAsync(context).ConfigureAwait(false);
                return;
            }

            if (!await staticFiles.TryServeAsync(context).ConfigureAwait(false))
            {
                await webApp.HandleAsync(context).ConfigureAwait(false);
            }
        };
    }

    public static void ValidateSeams(DomainSeams seams)
    {
        ArgumentNullException.ThrowIfNull(seams);
        if (IsNoOp(seams.Broadcaster))
        {
            throw new InvalidOperationException("DomainSeams.Broadcaster cannot be a no-op.");
        }
        if (IsNoOp(seams.Jobs))
        {
            throw new InvalidOperationException("DomainSeams.Jobs cannot be a no-op.");
        }
        if (IsNoOp(seams.Connections))
        {
            throw new InvalidOperationException("DomainSeams.Connections cannot be a no-op.");
        }
    }

    public static readonly IReadOnlyList<Type> EnqueuedJobTypes = typeof(Job).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(Job).IsAssignableFrom(t))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList();

    public static void ValidateJobs(JobRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var field = typeof(JobRunner).GetField("queues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var queues = (System.Collections.IDictionary?)field?.GetValue(runner);
        foreach (var jobType in EnqueuedJobTypes)
        {
            if (queues is null || !queues.Contains(jobType))
            {
                throw new InvalidOperationException($"No handler registered for job type {jobType.Name}.");
            }
        }
    }

    public static bool IsNoOp(object seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        var type = seam.GetType();
        var name = type.Name;
        return name.Contains("Null", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Discard", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Recording", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("NoOp", StringComparison.OrdinalIgnoreCase);
    }

    public static TimeProvider ResolveClock(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var fakeTimeStr = env("FAKETIME") ?? env("PARITY_TIME");
        if (!string.IsNullOrWhiteSpace(fakeTimeStr))
        {
            var cleaned = fakeTimeStr.Trim().TrimStart('@');
            if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return new OffsetClock(DateTimeOffset.UtcNow, parsed);
            }
        }
        return TimeProvider.System;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await JobRunner.DisposeAsync().ConfigureAwait(false);
        if (ownsPushPool)
        {
            await PushPool.DisposeAsync().ConfigureAwait(false);
        }
        if (ownsWebhookClient)
        {
            WebhookClient.Dispose();
        }
        if (ownsPushClient)
        {
            PushClient.Dispose();
        }
        CableServer.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
    static partial void LogJobInfo(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    static partial void LogJobWarn(ILogger logger, string message, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    static partial void LogJobError(ILogger logger, string message, Exception? exception);
}
