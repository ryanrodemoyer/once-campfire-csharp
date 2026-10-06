using System.Reflection;
using System.Text.Json;
using Campfire.Web.Routing;

namespace Campfire.Web.Tests.Routing;

public class UrlGenerationTests
{
    static readonly UrlBase Example = new("http", "example.com");

    [Theory]
    [MemberData(nameof(RouteVectors.Generations), MemberType = typeof(RouteVectors))]
    public void Path_helpers_generate_what_rails_generates(GenerationCase sample)
    {
        var route = Routes.Table.Named(sample.Helper[..^"_path".Length]);
        var arguments = sample.Arguments.Select(Value).ToArray();
        var options = Options(sample.Options);
        if (sample.Path.ValueKind == JsonValueKind.Object)
        {
            Assert.Throws<UrlGenerationException>(() => UrlGenerator.Path(route, arguments, options));
        }
        else
        {
            Assert.Equal(sample.Path.GetString(), UrlGenerator.Path(route, arguments, options));
        }
    }

    [Theory]
    [MemberData(nameof(RouteVectors.Directs), MemberType = typeof(RouteVectors))]
    public void Direct_routes_and_url_helpers_generate_what_rails_generates(DirectCase sample)
    {
        var options = Options(sample.Options);
        var actual = sample.Helper switch
        {
            "fresh_account_logo_path" => Routes.FreshAccountLogoPath(Time(sample.AccountUpdatedAt), options.ContainsKey("size") ? options["size"] : null),
            "fresh_user_avatar_path" => Routes.FreshUserAvatarPath(sample.User!.Value.GetProperty("avatar_token").GetString(), Time(sample.User!.Value.GetProperty("updated_at").GetString())!.Value),
            "fresh_user_avatar_url" => Routes.FreshUserAvatarUrl(Example, sample.User!.Value.GetProperty("avatar_token").GetString(), Time(sample.User!.Value.GetProperty("updated_at").GetString())!.Value),
            "room_url" => Routes.RoomUrl(Example, Value(sample.Arguments![0]), options),
            _ => throw new InvalidOperationException(sample.Helper),
        };
        Assert.Equal(sample.Path, actual);
    }

    [Fact]
    public void Every_named_route_has_typed_path_and_url_helpers()
    {
        var methods = typeof(Routes).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(method => method.Name).ToHashSet();
        foreach (var route in Routes.Table.Routes.Where(route => route.Name is not null))
        {
            Assert.Contains(RoutesSource.HelperName(route.Name!, "Path"), methods);
            Assert.Contains(RoutesSource.HelperName(route.Name!, "Url"), methods);
        }
    }

    [Fact]
    public void Typed_helpers_take_positional_segments_like_rails()
    {
        Assert.Equal("/", Routes.RootPath());
        Assert.Equal("/rooms/1/@42", Routes.RoomAtMessagePath(1, 42));
        Assert.Equal("/users/me/sidebar", Routes.UserSidebarPath());
        Assert.Equal("/users/7/sidebar", Routes.UserSidebarPath(7));
        Assert.Equal("/users/me/push_subscriptions/4/test_notifications", Routes.UserPushSubscriptionTestNotificationsPath(4));
        Assert.Equal("/rooms/1/abc/messages", Routes.RoomBotMessagesPath(1, "abc", new RouteOptions { { "format", "json" } }));
        Assert.Equal("/searches?q=hello+world", Routes.SearchesPath(new RouteOptions { { "q", "hello world" } }));
        Assert.Equal("/rooms/directs?user_ids%5B%5D=3", Routes.RoomsDirectsPath(new RouteOptions { { "user_ids", new List<int> { 3 } } }));
        Assert.Equal("/rooms/5", Routes.RoomPath(new Record(5)));
        Assert.Throws<UrlGenerationException>(() => Routes.RoomPath(new Record(null)));
        Assert.Equal("https://campfire.test/rooms/1", Routes.RoomUrl(new UrlBase("https://", "campfire.test", 443), 1));
        Assert.Equal("http://campfire.test:3000/rooms/1", Routes.RoomUrl(new UrlBase("http", "campfire.test", 3000), 1));
    }

    sealed record Record(long? Id) : IToParam
    {
        public string? ToParam() => Id?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static DateTimeOffset? Time(string? iso8601) =>
        iso8601 is null ? null : DateTimeOffset.Parse(iso8601, System.Globalization.CultureInfo.InvariantCulture);

    static RouteOptions Options(JsonElement options)
    {
        var result = new RouteOptions();
        foreach (var property in options.EnumerateObject())
        {
            result.Add(property.Name, Value(property.Value));
        }
        return result;
    }

    static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetInt64(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => value.EnumerateArray().Select(Value).ToList(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => Value(property.Value)),
        _ => throw new InvalidOperationException(value.ToString()),
    };
}
