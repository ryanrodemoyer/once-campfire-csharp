using System.Net;
using Campfire.Jobs.RestrictedHttp;

namespace Campfire.Jobs.Tests.RestrictedHttp;

// reference/test/lib/restricted_http/private_network_guard_test.rb
public sealed class PrivateNetworkGuardTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")] // "This" network (RFC1700)
    [InlineData("127.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")] // loopback
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")] // RFC1918
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")] // AWS IMDS
    [InlineData("169.254.255.255")] // link-local
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:172.16.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:93.184.216.34")] // every IPv4-mapped address: DNS never returns them legitimately
    [InlineData("::192.168.1.1")]
    [InlineData("::10.0.0.1")]
    [InlineData("::169.254.169.254")]
    [InlineData("::93.184.216.34")] // every IPv4-compatible address
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")] // carrier-grade NAT (RFC6598)
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b::a00:5")] // NAT64 embedding a private IPv4
    [InlineData("64:ff9b:1::a00:1")]
    [InlineData("64:ff9b:1::808:808")]
    [InlineData("64:ff9b:1:ffff::1")] // the whole local-use NAT64 block (RFC8215)
    [InlineData("::ffff:0:169.254.169.254")]
    [InlineData("::ffff:0:a9fe:a9fe")]
    [InlineData("::ffff:0:127.0.0.1")]
    [InlineData("::ffff:0:192.168.0.1")] // SIIT (RFC2765)
    [InlineData("2002:a9fe:a9fe::")] // 6to4
    [InlineData("2001::1")] // Teredo
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")] // AWS IMDSv6 (ULA)
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")] // benchmarking (RFC5180)
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void PrivateIp(string address) => Assert.True(PrivateNetworkGuard.IsPrivateIp(address), $"Expected {address} to be classified as private");

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("64:ff9b::808:808")] // DNS64 synthesizes these for public sites on IPv6-only hosts
    [InlineData("2606:4700:4700::1111")]
    public void PublicIp(string address) => Assert.False(PrivateNetworkGuard.IsPrivateIp(address));

    [Fact]
    public async Task ResolveRaisesViolationForPrivateHostname()
    {
        var guard = new PrivateNetworkGuard(FakeResolver.Of("192.168.1.1"));
        var violation = await Assert.ThrowsAsync<PrivateNetworkViolationException>(() => guard.ResolveAsync("private.example.com", TestContext.Current.CancellationToken));
        Assert.Equal("Attempt to access private IP via private.example.com", violation.Message);
    }

    [Fact]
    public async Task ResolveReturnsIpForPublicHostname()
    {
        var guard = new PrivateNetworkGuard(FakeResolver.Of("93.184.216.34"));
        Assert.Equal(IPAddress.Parse("93.184.216.34"), await guard.ResolveAsync("example.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveRaisesUnresolvableNotViolationWhenTheHostResolvesToNothing()
    {
        var guard = new PrivateNetworkGuard(FakeResolver.Of());
        await Assert.ThrowsAsync<UnresolvableHostException>(() => guard.ResolveAsync("nxdomain.example.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolvePinsThePublicAnswerWhenAnswersAreMixed()
    {
        var guard = new PrivateNetworkGuard(FakeResolver.Of("127.0.0.1", "fd00::1", "93.184.216.34"));
        Assert.Equal(IPAddress.Parse("93.184.216.34"), await guard.ResolveAsync("rebind.example.com", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("127.1")]
    [InlineData("0177.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:169.254.169.254]")]
    [InlineData("169.254.169.254")]
    public async Task NumericPrivateHostsAreViolationsWithoutDns(string host)
    {
        var resolver = FakeResolver.Of("93.184.216.34");
        await Assert.ThrowsAsync<PrivateNetworkViolationException>(() => new PrivateNetworkGuard(resolver).ResolveAsync(host, TestContext.Current.CancellationToken));
        Assert.Empty(resolver.Lookups);
    }

    [Fact]
    public void ZonedAddressesArePrivate()
    {
        Assert.True(PrivateNetworkGuard.IsPrivateIp(new IPAddress(IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes(), scopeid: 2)));
        Assert.True(PrivateNetworkGuard.IsPrivateIp("2606:4700:4700::1111%2"));
    }

    [Fact]
    public async Task ZonedAnswerMakesTheHostUnresolvable()
    {
        var zoned = new IPAddress(IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes(), scopeid: 2);
        var guard = new PrivateNetworkGuard(FakeResolver.Answering(IPAddress.Parse("93.184.216.34"), zoned));
        await Assert.ThrowsAsync<UnresolvableHostException>(() => guard.ResolveAsync("example.com", TestContext.Current.CancellationToken));
    }
}
