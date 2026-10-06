using Campfire.Vectors;

namespace Campfire.Web.Tests.Routing;

public class RoutesSourceTests
{
    static readonly string GeneratedPath = Path.Combine(VectorFiles.Root, "src/Campfire.Web/Routes.g.cs");

    [Fact]
    public void Named_routes_are_the_reference_route_table()
    {
        var named = RouteVectors.File.Routes.Select(route => (route.Verb, route.Path, route.Endpoint, Defaults: Sorted(route.Defaults)));
        var table = CampfireVectors.RoutesFile.Routes.Select(route => (route.Verb, route.Path, route.Endpoint, Defaults: Sorted(route.Defaults)));
        Assert.Equal(table, named);
    }

    [Fact]
    public void Routes_g_cs_is_generated_from_the_route_vectors()
    {
        var generated = RoutesSource.Generate(RouteVectors.File.Routes);
        if (Environment.GetEnvironmentVariable("UPDATE_ROUTES") == "1")
        {
            File.WriteAllText(GeneratedPath, generated);
        }
        Assert.Equal(generated, File.ReadAllText(GeneratedPath).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Every_endpoint_has_its_own_hook()
    {
        var endpoints = RouteVectors.File.Routes.Select(route => route.Endpoint).Distinct().ToList();
        Assert.Equal(endpoints.Count, endpoints.Select(RoutesSource.HookName).Distinct().Count());
    }

    static string Sorted(IReadOnlyDictionary<string, string> defaults) =>
        string.Join(",", defaults.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
}
