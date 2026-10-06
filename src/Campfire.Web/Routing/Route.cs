using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Routing;

/// <summary>
/// One row of <c>bin/rails routes</c>: the verb, the path spec, <c>controller#action</c>, the route
/// name when it has one, and its non-routing defaults (<c>format: :json</c> under the bot scope,
/// <c>user_id: "me"</c> under <c>scope defaults:</c>).
/// </summary>
public sealed record RouteDefinition(
    string Verb,
    string Path,
    string Endpoint,
    string? Name = null,
    IReadOnlyList<KeyValuePair<string, string>>? Defaults = null);

/// <summary>A compiled <see cref="RouteDefinition"/> bound to its handler.</summary>
public sealed class Route
{
    internal Route(int index, RouteDefinition definition, RequestDelegate handler)
    {
        Index = index;
        Definition = definition;
        Spec = RouteSpec.Parse(definition.Path);
        var hash = definition.Endpoint.IndexOf('#', StringComparison.Ordinal);
        Controller = definition.Endpoint[..hash];
        Action = definition.Endpoint[(hash + 1)..];
        Defaults = definition.Defaults ?? [];
        Handler = handler;
    }

    /// <summary>The route's position in the table: Rails' precedence.</summary>
    public int Index { get; }

    public RouteDefinition Definition { get; }

    public string Verb => Definition.Verb;

    public string Endpoint => Definition.Endpoint;

    public string? Name => Definition.Name;

    public RouteSpec Spec { get; }

    public string Controller { get; }

    public string Action { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Defaults { get; }

    public RequestDelegate Handler { get; }

    public string? DefaultFor(string key)
    {
        foreach (var (name, value) in Defaults)
        {
            if (name == key)
            {
                return value;
            }
        }
        return null;
    }

    public override string ToString() => $"{Verb} {Spec} {Endpoint}";
}
