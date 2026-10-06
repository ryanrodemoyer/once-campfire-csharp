using Campfire.Jobs.RestrictedHttp;
using Campfire.Jobs.Tests.RestrictedHttp;
using Campfire.Jobs.WebPush;

namespace Campfire.Jobs.Tests.WebPush;

// reference/test/models/push/subscription_test.rb: the endpoint validation and
// `resolved_endpoint_ip`, with `stub_web_push_dns_resolution` as the default answer.
public sealed class PushEndpointTests
{
    /// <summary><c>DnsTestHelper::WEB_PUSH_PUBLIC_TEST_IP</c></summary>
    public const string PublicTestIp = "142.250.185.206";

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    static PrivateNetworkGuard Guard(params string[] answer) => new(FakeResolver.Of(answer.Length == 0 ? [PublicTestIp] : answer));

    static Task<List<string>> Errors(string? endpoint, PrivateNetworkGuard? guard = null) =>
        PushEndpoint.ValidationErrorsAsync(guard ?? Guard(), endpoint, Cancellation);

    [Fact]
    public async Task Valid_subscription_with_permitted_endpoint() =>
        Assert.Empty(await Errors("https://fcm.googleapis.com/fcm/send/abc123"));

    [Fact]
    public async Task Rejects_endpoint_with_non_https_scheme() =>
        Assert.Contains("must use HTTPS", await Errors("http://fcm.googleapis.com/fcm/send/abc123"));

    [Fact]
    public async Task Rejects_endpoint_with_non_permitted_host() =>
        Assert.Contains("is not a permitted push service", await Errors("https://attacker.example.com/webhook"));

    [Fact]
    public async Task Rejects_endpoint_whose_host_only_suffix_matches_a_permitted_host() =>
        Assert.Contains("is not a permitted push service", await Errors("https://evilfcm.googleapis.com.attacker.example/webhook"));

    [Fact]
    public async Task Rejects_blank_endpoint() =>
        Assert.Contains("can't be blank", await Errors(""));

    [Fact]
    public async Task Rejects_endpoint_on_a_non_default_port() =>
        Assert.Contains("must use the default HTTPS port", await Errors("https://fcm.googleapis.com:8443/fcm/send/abc123"));

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task Rejects_endpoint_that_resolves_to_a_private_address(string ip) =>
        Assert.Contains("resolves to a private or invalid IP address", await Errors("https://fcm.googleapis.com/fcm/send/abc123", Guard(ip)));

    [Fact]
    public async Task Rejects_endpoint_whose_host_resolves_to_nothing_without_raising()
    {
        var guard = new PrivateNetworkGuard(FakeResolver.Answering());

        Assert.Null(await PushEndpoint.ResolveAsync(guard, "https://fcm.googleapis.com/fcm/send/abc123", Cancellation));
        Assert.Contains("resolves to a private or invalid IP address", await Errors("https://fcm.googleapis.com/fcm/send/abc123", guard));
    }

    [Fact]
    public async Task Resolved_endpoint_ip_returns_the_pinned_public_ip() =>
        Assert.Equal(PublicTestIp, (await PushEndpoint.ResolveAsync(Guard(), "https://fcm.googleapis.com/fcm/send/abc123", Cancellation))?.ToString());

    // "delivery is skipped for a non-permitted host even when it resolves publicly" and "... for a
    // permitted host on a non-default port": no lookup, no address.
    [Theory]
    [InlineData("https://attacker.example.com/collect")]
    [InlineData("https://fcm.googleapis.com:22/fcm/send/abc123")]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc123")]
    [InlineData("not a url")]
    [InlineData(null)]
    public async Task Resolves_nothing_for_an_endpoint_that_is_not_permitted(string? endpoint)
    {
        var resolver = FakeResolver.Of(PublicTestIp);

        Assert.Null(await PushEndpoint.ResolveAsync(new PrivateNetworkGuard(resolver), endpoint, Cancellation));
        Assert.Empty(resolver.Lookups);
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/token123")]
    [InlineData("https://jmt17.google.com/fcm/send/token123")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/token123")]
    [InlineData("https://web.push.apple.com/QaBC123")]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc123")]
    [InlineData("https://FCM.GoogleApis.com/fcm/send/token123")]
    public async Task Accepts_all_permitted_push_service_domains(string endpoint) =>
        Assert.Empty(await Errors(endpoint));
}
