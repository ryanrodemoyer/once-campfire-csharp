using Campfire.Server.Cli;
using Microsoft.AspNetCore.Http;

namespace Campfire.Server.Tests.Cli;

public sealed class RailsSslTests
{
    [Fact]
    public void Adds_hsts_unless_the_app_set_its_own()
    {
        var headers = new HeaderDictionary();
        RailsSsl.FlagResponse(headers);
        Assert.Equal("max-age=63072000; includeSubDomains", headers["strict-transport-security"]);

        var own = new HeaderDictionary { ["Strict-Transport-Security"] = "max-age=1" };
        RailsSsl.FlagResponse(own);
        Assert.Equal("max-age=1", own["strict-transport-security"]);
    }

    [Fact]
    public void Flags_every_cookie_secure_once()
    {
        var headers = new HeaderDictionary
        {
            ["set-cookie"] = new([
                "a=1; path=/",
                "b=2; path=/; Secure",
                "c=3; secure; httponly",
                "d=4; path=/secured",
                "e=5; secure ",
            ]),
        };

        RailsSsl.FlagResponse(headers);

        Assert.Equal(
            ["a=1; path=/; secure", "b=2; path=/; Secure", "c=3; secure; httponly", "d=4; path=/secured; secure", "e=5; secure "],
            headers["set-cookie"].Select(cookie => cookie!).ToArray());
    }

    [Fact]
    public void Leaves_responses_without_cookies_alone()
    {
        var headers = new HeaderDictionary();
        RailsSsl.FlagResponse(headers);
        Assert.False(headers.ContainsKey("set-cookie"));
    }
}
