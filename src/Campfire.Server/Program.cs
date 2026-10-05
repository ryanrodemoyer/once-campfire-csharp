namespace Campfire.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = Build(args);
          await app.RunAsync();
    }

    // Requests go to a raw RequestDelegate, not MVC or minimal-API endpoints. The ordered
    // Rails-compatible router (W01) replaces this placeholder.
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    static Task HandleAsync(HttpContext context)
    {
        context.Response.StatusCode = context.Request.Path == "/up"
            ? StatusCodes.Status200OK
            : StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
