using System.Net;

namespace Campfire.Jobs.Tests.WebPush;

/// <summary>
/// A push service in place of the network, for the pool and pusher tests (the TLS path is
/// <see cref="PushServiceServer"/>'s): records each request and answers with <see cref="Status"/>,
/// once <see cref="Gate"/> opens.
/// </summary>
sealed class RecordingPushHandler : HttpMessageHandler
{
    public sealed record Sent(Uri Uri, IPAddress? PinnedAddress, byte[] Body);

    readonly List<Sent> sent = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

    public Task Gate { get; set; } = Task.CompletedTask;

    public IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (sent)
            {
                return [.. sent];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(Campfire.Jobs.RestrictedHttp.RestrictedHttpHandlers.PinnedAddress, out var pinned);
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        lock (sent)
        {
            sent.Add(new Sent(request.RequestUri!, pinned, body));
        }
        await Gate.WaitAsync(cancellationToken);
        return new HttpResponseMessage(Status);
    }
}
