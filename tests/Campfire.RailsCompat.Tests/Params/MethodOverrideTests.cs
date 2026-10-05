using System.Text;
using Campfire.RailsCompat.Params;

namespace Campfire.RailsCompat.Tests.Params;

public class MethodOverrideTests
{
    static async Task<string> Resolve(string method, string? contentType, string body, string? header = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var parsed = await RequestBody.ParseAsync(method, contentType, bytes.Length, new MemoryStream(bytes), cancellationToken: TestContext.Current.CancellationToken);
        return MethodOverride.Resolve(method, contentType, parsed, header);
    }

    [Theory]
    [InlineData("POST", "application/x-www-form-urlencoded", "_method=patch", null, "PATCH")]
    [InlineData("POST", "application/x-www-form-urlencoded", "_method=delete&x=1", null, "DELETE")]
    [InlineData("POST", null, "_method=put", null, "PUT")]
    [InlineData("POST", "application/x-www-form-urlencoded", "_method=bogus", null, "POST")]
    [InlineData("POST", "application/x-www-form-urlencoded", "_method[]=patch", null, "POST")]
    [InlineData("GET", "application/x-www-form-urlencoded", "_method=delete", null, "GET")]
    [InlineData("POST", "application/x-www-form-urlencoded", "x=1", "patch", "PATCH")]
    [InlineData("POST", "application/json", """{"_method":"delete"}""", null, "POST")]
    [InlineData("POST", "application/json", """{"_method":"delete"}""", "DELETE", "DELETE")]
    // Unparseable params are rescued, falling back to the header.
    [InlineData("POST", "application/x-www-form-urlencoded", "_method=patch&a=%", "put", "PUT")]
    public async Task OverridesAPostLikeRack(string method, string? contentType, string body, string? header, string expected)
    {
        Assert.Equal(expected, await Resolve(method, contentType, body, header));
    }

    [Fact]
    public async Task MultipartFormsCarryItToo()
    {
        var body = "--B\r\nContent-Disposition: form-data; name=\"_method\"\r\n\r\ndelete\r\n--B--\r\n";
        Assert.Equal("DELETE", await Resolve("POST", "multipart/form-data; boundary=B", body));
    }
}
