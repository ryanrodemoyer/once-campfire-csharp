using System.Net;
using System.Net.Sockets;
using Campfire.Jobs.OpenGraph;
using Campfire.Jobs.RestrictedHttp;
using Campfire.Vectors;

namespace Campfire.Jobs.Tests.OpenGraph;

/// <summary>
/// Replays vectors/opengraph/cases.json (fake DNS, a scripted server behind the fake public
/// addresses) and compares the unfurl response, every DNS lookup and every HTTP request with what
/// the reference did for the same case (vectors/opengraph/expected.json).
/// </summary>
public sealed class OpenGraphVectorTests
{
    static OpenGraphCasesFile Spec => IntegrationVectors.OpenGraphCasesFile;

    public static TheoryData<OpenGraphExpectedCase> Cases() => IntegrationVectors.OpenGraph();

    [Fact]
    public void CoversEveryCase()
    {
        Assert.Equal(90, IntegrationVectors.OpenGraphExpectedFile.Count);
        Assert.Equal(Spec.Cases.Select(c => (c.Name, c.Url)), IntegrationVectors.OpenGraphExpectedFile.Select(c => (c.Name, c.Url)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task UnfurlsLikeTheReference(OpenGraphExpectedCase expected)
    {
        await using var server = new ScriptedServer(Spec.Routes);
        var resolver = new ScriptedResolver(Spec.Hosts);
        var publicIps = Spec.PublicIps.Select(IPAddress.Parse).ToHashSet();
        using var fetch = new OpenGraphFetch(new PrivateNetworkGuard(resolver), (endpoint, cancellationToken) =>
            publicIps.Contains(endpoint.Address)
                ? server.ConnectAsync(cancellationToken)
                : throw new InvalidOperationException($"dialled {endpoint}"));

        OpenGraphResponse response;
        try
        {
            var json = await OpenGraphMetadata.UnfurlAsync(fetch, expected.Url, TestContext.Current.CancellationToken);
            response = json is null ? new(204) : new(200, json);
        }
        catch (OpenGraphRaisedException e)
        {
            response = new(500, Error: e.RubyClass);
        }

        Assert.Equal(expected.Response, response);
        Assert.Equal(expected.Lookups, resolver.Lookups);
        Assert.Equal(expected.Requests.Select(r => r.ToArray()), server.Requests);
    }

    /// <summary>
    /// The oracle's <c>Resolv.getaddresses</c>: each lookup of a host takes its next answer while
    /// more than one is left, then keeps the last; an unknown host doesn't resolve.
    /// </summary>
    sealed class ScriptedResolver(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> hosts) : IResolver
    {
        readonly Dictionary<string, Queue<IReadOnlyList<string>>> answers =
            hosts.ToDictionary(host => host.Key, host => new Queue<IReadOnlyList<string>>(host.Value));

        public List<string> Lookups { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Lookups.Add(host);
            if (!answers.TryGetValue(host, out var queue))
            {
                return Task.FromException<IReadOnlyList<IPAddress>>(new SocketException((int)SocketError.HostNotFound));
            }
            var answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Task.FromResult<IReadOnlyList<IPAddress>>([.. answer.Select(IPAddress.Parse)]);
        }
    }
}
