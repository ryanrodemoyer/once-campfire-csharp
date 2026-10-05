using System.Net;
using System.Net.Sockets;
using Campfire.Jobs.RestrictedHttp;

namespace Campfire.Jobs.Tests.RestrictedHttp;

/// <summary>
/// Answers each lookup with the next of its fixed answers (the last one repeats). An empty answer
/// is NXDOMAIN.
/// </summary>
sealed class FakeResolver(IPAddress[][] answers) : IResolver
{
    int calls;

    public List<string> Lookups { get; } = [];

    public static FakeResolver Answering(params IPAddress[] answer) => new([answer]);

    public static FakeResolver Of(params string[] answer) => Answering([.. answer.Select(IPAddress.Parse)]);

    /// <summary>One answer per lookup, in order: a DNS server that changes its mind.</summary>
    public static FakeResolver Sequence(params string[][] answers) => new([.. answers.Select(answer => answer.Select(IPAddress.Parse).ToArray())]);

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Lookups.Add(host);
        var answer = answers[Math.Min(calls++, answers.Length - 1)];
        return answer.Length == 0
            ? Task.FromException<IReadOnlyList<IPAddress>>(new SocketException((int)SocketError.HostNotFound))
            : Task.FromResult<IReadOnlyList<IPAddress>>(answer);
    }
}
