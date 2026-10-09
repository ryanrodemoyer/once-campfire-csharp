using System.Net;
using System.Net.Sockets;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.WebPush;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Controllers.PushSubscriptions;

/// <summary>
/// DNS answers for <c>Push::Subscription</c> validation and delivery. <c>"unresolvable"</c> or null
/// is a failed lookup; anything else is one address (<c>Resolv.getaddresses</c> in the oracle).
/// </summary>
sealed class ScriptedResolver : IResolver
{
    public string? Answer { get; set; } = "142.250.185.206";

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Answer) || Answer == "unresolvable")
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse(Answer)]);
    }
}

static class PushSubscriptionClients
{
    public static WebPushClient Open(ScriptedResolver resolver, MessagesApp app)
    {
        var guard = new PrivateNetworkGuard(resolver);
        return new WebPushClient(
            RestrictedHttpHandlers.WebPush(guard),
            guard,
            new VapidIdentification(MessagesApp.ParityEnvironment("VAPID_PUBLIC_KEY"), MessagesApp.ParityEnvironment("VAPID_PRIVATE_KEY")),
            app.App.Clock);
    }
}
