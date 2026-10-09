using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.WebPush;

namespace Campfire.Web.Pipeline;

// The Web Push client test notifications and subscription checks use. The server (P01) may set
// <see cref="WebApp.WebPush"/> and dispose it; until it does, a request opens one from the
// environment the way Rails opens a connection for <c>WebPush.payload_send</c>.
public sealed partial class WebApp
{
    /// <summary>
    /// The app's <c>WebPush.payload_send</c> client, when the server or a test has one.
    /// Unset, <see cref="OpenPushClient"/> builds a client for that one delivery.
    /// </summary>
    public WebPushClient? WebPush { get; set; }

    /// <summary>The guard subscription validation resolves endpoints through.</summary>
    internal PrivateNetworkGuard PushGuard => WebPush?.Guard ?? PrivateNetworkGuard.System;

    /// <summary>
    /// A client for one delivery when <see cref="WebPush"/> isn't set. The caller disposes it.
    /// </summary>
    internal WebPushClient OpenPushClient()
    {
        var guard = PrivateNetworkGuard.System;
        return new WebPushClient(RestrictedHttpHandlers.WebPush(guard), guard, VapidIdentification.FromEnvironment(), Clock);
    }
}
