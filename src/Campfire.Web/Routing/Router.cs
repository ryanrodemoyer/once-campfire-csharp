using Campfire.RailsCompat.Params;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Routing;

/// <summary>
/// The app's single <see cref="RequestDelegate"/>: it parses the body once (a POST's
/// <c>_method</c> picks the verb, as <c>Rack::MethodOverride</c> does), finds the first matching
/// route, installs the <see cref="RailsRequest"/> feature and runs the route's handler. Exceptions
/// become Rails' error responses (<see cref="ErrorPages"/>); no route is a 404.
/// </summary>
public sealed class Router(RouteTable routes, ErrorPages errorPages, string? uploadDirectory = null)
{
    public RouteTable Routes { get; } = routes;

    public async Task HandleAsync(HttpContext context)
    {
        using var request = new RailsRequest(context);
        context.Features.Set(request);
        try
        {
            await DispatchAsync(context, request).ConfigureAwait(false);
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            await errorPages.WriteAsync(context, error).ConfigureAwait(false);
        }
    }

    async Task DispatchAsync(HttpContext context, RailsRequest request)
    {
        // Rack::MethodOverride runs before routing and only reads a POST's body.
        if (request.WireMethod == "POST")
        {
            await ReadBodyAsync(context, request).ConfigureAwait(false);
            request.Method = MethodOverride.Resolve(request.WireMethod, context.Request.ContentType, request.Body, context.Request.Headers["X-HTTP-Method-Override"].FirstOrDefault());
        }

        // DebugExceptions turns the router's X-Cascade 404 into a RoutingError.
        var match = Routes.Recognize(request.Method, request.Path)
            ?? throw new RoutingErrorException($"No route matches [{request.WireMethod}] \"{request.OriginalPath}\"");
        request.Matched(match);
        if (request.WireMethod != "POST")
        {
            await ReadBodyAsync(context, request).ConfigureAwait(false);
        }
        await match.Route.Handler(context).ConfigureAwait(false);
    }

    async Task ReadBodyAsync(HttpContext context, RailsRequest request) =>
        request.Body = await RequestBody.ParseAsync(
            request.WireMethod,
            context.Request.ContentType,
            context.Request.ContentLength,
            context.Request.Body,
            uploadDirectory,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
}
