using Campfire.Jobs.OpenGraph;
using Campfire.Jobs.RestrictedHttp;
using Microsoft.Extensions.Logging;

namespace Campfire.Web.Pipeline;

// The Open Graph client UnfurlLinksController uses. The server or a test may set
// <see cref="WebApp.OpenGraphFetch"/> and dispose it; until it does, an instance is lazily opened
// over <see cref="PrivateNetworkGuard.System"/>.
public sealed partial class WebApp
{
    OpenGraphFetch? openGraphFetch;

    /// <summary>
    /// The Open Graph client used for link unfurling. Tests may set this, or it falls back
    /// to a shared instance using <see cref="PrivateNetworkGuard.System"/>.
    /// </summary>
    public OpenGraphFetch OpenGraphFetch
    {
        get => openGraphFetch ??= new OpenGraphFetch(PrivateNetworkGuard.System)
        {
            Warn = message => LogOpenGraphWarning(Logger, message),
        };
        set => openGraphFetch = value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    static partial void LogOpenGraphWarning(ILogger logger, string message);
}
