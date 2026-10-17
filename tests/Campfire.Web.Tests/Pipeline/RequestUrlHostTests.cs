using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Pipeline;

/// <summary>
/// <c>request.host</c>, <c>request.port</c> and <c>request.base_url</c> (<c>ActionDispatch::Http::URL</c>) come from the
/// raw host by <c>/:(\d+)$/</c>. That was a regular expression run up to three times for every <c>BaseUrl</c>, and a base URL is
/// asked for several times a request, by the forgery check and the response cache key among others. It is now read from the
/// end of the host and worked out once; <see cref="Before"/> is the expression, and the two must split every host alike,
/// Unicode digits and a final newline (where <c>$</c> also matches) included.
/// </summary>
public class RequestUrlHostTests
{
    static readonly char[] Alphabet =
        ("abcXYZ019:::..-[]\n \t" + (char)0x663 + (char)0xFF11 + (char)0x0e55 + "80").ToCharArray();

    static RequestUrl For(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers.Host = host;
        return new RequestUrl(context.Request);
    }

    [Fact]
    public void A_host_is_split_as_the_expression_split_it()
    {
        var random = new Random(20261101);
        var hosts = new List<string> { "", ":", "a:", ":80", "a:80", "a:80\n", "a:80\n\n", "a:8x", "a:1:2", "[::1]:3000", "[::1]", "campfire.test:" + (char)0x663 };
        for (var i = 0; i < 20_000; i++)
        {
            var builder = new StringBuilder();
            for (var length = random.Next(14); length > 0; length--)
            {
                builder.Append(Alphabet[random.Next(Alphabet.Length)]);
            }
            hosts.Add(builder.ToString());
        }

        // (An empty Host header is no header: the request takes the server's own name.)
        foreach (var host in hosts.Where(host => host.Length > 0))
        {
            var url = For(host);
            var expectedHost = Before.Host(host);
            Assert.True(expectedHost == url.Host, $"host of {Escaped(host)}: expected {Escaped(expectedHost)}, got {Escaped(url.Host)}");
            Assert.True(Before.Port(host, url.StandardPort) == url.Port, $"port of {Escaped(host)}");
            Assert.Equal(Before.BaseUrl(host, url.StandardPort), url.BaseUrl);
        }
    }

    static string Escaped(string text) => string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : "\\x" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)));

    [Fact]
    public void A_base_url_is_the_same_string_every_time()
    {
        var url = For("campfire.test:3000");
        Assert.Equal("http://campfire.test:3000", url.BaseUrl);
        Assert.Same(url.BaseUrl, url.BaseUrl);
        Assert.Equal("campfire.test", url.Host);
        Assert.Equal(3000, url.Port);
        Assert.Equal("http://campfire.test", For("campfire.test:80").BaseUrl);
        Assert.Equal("http://campfire.test", For("campfire.test").BaseUrl);
    }

    /// <summary>The expression these replaced.</summary>
    static class Before
    {
        static readonly Regex TrailingPort = new(@":(\d+)$");

        public static string Host(string raw) => TrailingPort.Replace(raw, "", 1);

        public static int Port(string raw, int standard)
        {
            var match = TrailingPort.Match(raw);
            return match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : standard;
        }

        public static string BaseUrl(string raw, int standard)
        {
            var host = Host(raw);
            var port = Port(raw, standard);
            return "http://" + (port == standard ? host : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
