using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Routing;

/// <summary>The route that answered a request and its <c>path_parameters</c>.</summary>
public sealed record RouteMatch(Route Route, RouteParameters Parameters);

/// <summary>
/// The ordered route table and Journey's recognition (actionpack <c>journey/router.rb</c>): of the
/// routes whose verb and pattern match, the first in <c>config/routes.rb</c> order wins. That is
/// why <c>GET /rooms/opens</c> is <c>rooms#show</c> with <c>id: "opens"</c>: <c>resources :rooms</c>
/// is drawn before <c>namespace :rooms</c>. HEAD requests match GET routes.
/// </summary>
public sealed class RouteTable
{
    readonly Dictionary<string, Route[]> byVerb;
    readonly Dictionary<string, Route> byName;

    /// <param name="definitions">The routes, in Rails order.</param>
    /// <param name="handlerFor">Each route's handler; null leaves the 501 stub.</param>
    public RouteTable(IEnumerable<RouteDefinition> definitions, Func<RouteDefinition, RequestDelegate?> handlerFor)
    {
        Routes = definitions
            .Select((definition, index) => new Route(index, definition, handlerFor(definition) ?? NotImplemented))
            .ToArray();
        byVerb = Routes.GroupBy(route => route.Verb, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        byName = Routes.Where(route => route.Name is not null)
            .ToDictionary(route => route.Name!, StringComparer.Ordinal);
    }

    /// <summary>Every route, in Rails order.</summary>
    public IReadOnlyList<Route> Routes { get; }

    /// <summary>The route named <paramref name="name"/> (the <c>as:</c> of <c>name_path</c>).</summary>
    public Route Named(string name) =>
        byName.TryGetValue(name, out var route) ? route : throw new KeyNotFoundException($"No route named {name}");

    /// <summary>
    /// The first route for <paramref name="method"/> whose pattern matches the normalized
    /// <paramref name="path"/>, or null. Throws <see cref="UnknownHttpMethodException"/> for a verb
    /// Rails doesn't know when some route's pattern matches the path (Rails only asks the request
    /// for its method once a pattern has matched), and <see cref="BadRequestException"/> when a
    /// captured parameter isn't valid UTF-8 once decoded.
    /// </summary>
    public RouteMatch? Recognize(string method, string path)
    {
        if (!HttpErrors.KnownMethods.Contains(method))
        {
            if (Routes.Any(route => route.Spec.Regex.IsMatch(path)))
            {
                throw new UnknownHttpMethodException($"{method}, accepted HTTP methods are {string.Join(", ", HttpErrors.KnownMethods)}");
            }
            return null;
        }

        var verb = method == "HEAD" ? "GET" : method;
        if (!byVerb.TryGetValue(verb, out var candidates))
        {
            return null;
        }
        foreach (var route in candidates)
        {
            if (!path.StartsWith(route.Spec.LiteralPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var match = route.Spec.Regex.Match(path);
            if (match.Success)
            {
                return new RouteMatch(route, PathParameters(route, match));
            }
        }
        return null;
    }

    static RouteParameters PathParameters(Route route, System.Text.RegularExpressions.Match match)
    {
        var parameters = new RouteParameters();
        foreach (var (key, value) in route.Defaults)
        {
            parameters.Set(key, value);
        }
        parameters.Set("controller", route.Controller);
        parameters.Set("action", route.Action);
        var names = route.Spec.Names;
        for (var i = 0; i < names.Count; i++)
        {
            var group = match.Groups[i + 1];
            if (group.Success)
            {
                parameters.Set(names[i], PathEscaping.UnescapePathParameter(names[i], group.Value));
            }
        }
        return parameters;
    }

    static Task NotImplemented(HttpContext context)
    {
        var endpoint = context.RailsRequest().Route?.Endpoint;
        throw new NotImplementedRouteException($"{endpoint} is not implemented yet");
    }
}
