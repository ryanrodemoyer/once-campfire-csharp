using System.Net;
using System.Text.Json;
using Campfire.Jobs.RestrictedHttp;

namespace Campfire.Jobs.Tests.RestrictedHttp;

// Vectors/surfguard.json is the surfguard gem's own output (Vectors/generate.rb): its conformance
// corpus, reference/test/lib/restricted_http/private_network_guard_test.rb, host spellings, the
// edges of every range in the policy, and random addresses.
public sealed class SurfguardVectorTests
{
    static readonly JsonElement Vectors = Load();

    public static TheoryData<int> Hosts() => [.. Enumerable.Range(0, Vectors.GetProperty("hosts").GetArrayLength())];

    public static TheoryData<int> Answers() => [.. Enumerable.Range(0, Vectors.GetProperty("answers").GetArrayLength())];

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task HostMatchesTheGem(int index)
    {
        var vector = Vectors.GetProperty("hosts")[index];
        var input = vector.GetProperty("input").GetString()!;
        var resolver = FakeResolver.Of(Vectors.GetProperty("dns_answer").GetString()!);

        Assert.Equal(vector.GetProperty("blocked").GetBoolean(), Surfguard.IsBlockedAddress(input));

        if (vector.TryGetProperty("error", out var error))
        {
            Assert.Equal("unresolvable", error.GetString());
            await Assert.ThrowsAsync<UnresolvableHostException>(() => Surfguard.ResolvePublicIpsAsync(resolver, input, TestContext.Current.CancellationToken));
        }
        else
        {
            var ips = await Surfguard.ResolvePublicIpsAsync(resolver, input, TestContext.Current.CancellationToken);
            Assert.Equal(Addresses(vector.GetProperty("ips")), ips);
        }
        Assert.Equal(vector.GetProperty("dns").GetBoolean(), resolver.Lookups.Count > 0);
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task ResolverAnswersMatchTheGem(int index)
    {
        var vector = Vectors.GetProperty("answers")[index];
        var resolver = FakeResolver.Answering([.. Addresses(vector.GetProperty("answers"))]);

        if (vector.TryGetProperty("error", out var error))
        {
            Assert.Equal("unresolvable", error.GetString());
            await Assert.ThrowsAsync<UnresolvableHostException>(() => Surfguard.ResolvePublicIpsAsync(resolver, "example.com", TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(Addresses(vector.GetProperty("ips")), await Surfguard.ResolvePublicIpsAsync(resolver, "example.com", TestContext.Current.CancellationToken));
        }
    }

    static List<IPAddress> Addresses(JsonElement array) => [.. array.EnumerateArray().Select(ip => IPAddress.Parse(ip.GetString()!))];

    static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Campfire.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("Campfire.slnx not found above the test binaries");
        }
        var path = Path.Combine(directory.FullName, "tests/Campfire.Jobs.Tests/RestrictedHttp/Vectors/surfguard.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}
