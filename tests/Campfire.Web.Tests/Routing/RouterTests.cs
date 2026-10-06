using System.Text;
using Campfire.Vectors;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Routing;

public class RouterTests
{
    static readonly ErrorPages ReferencePages = ErrorPages.FromDirectory(Path.Combine(VectorFiles.Root, "reference/public"));

    [Theory]
    [MemberData(nameof(RouteVectors.Formats), MemberType = typeof(RouteVectors))]
    public void Negotiates_formats_like_rails(FormatsCase sample)
    {
        var context = Request("GET", sample.Format is null ? sample.Path : $"{sample.Path}?format={sample.Format}");
        if (sample.Accept is not null)
        {
            context.Request.Headers.Accept = sample.Accept;
        }
        if (sample.Xhr)
        {
            context.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        }
        context.Request.ContentType = sample.ContentType;
        using var request = new RailsRequest(context);
        Assert.Equal(sample.Formats, request.Formats.Select(format => format.Value));
    }

    [Theory]
    [MemberData(nameof(RouteVectors.PublicExceptions), MemberType = typeof(RouteVectors))]
    public void Error_bodies_for_json_and_xml_are_rails(PublicExceptionCase sample)
    {
        foreach (var (extension, expected, type) in new[] { ("json", sample.Json, "application/json"), ("xml", sample.Xml, "application/xml") })
        {
            using var request = new RailsRequest(Request("GET", "/nope." + extension));
            var response = ReferencePages.For(sample.Status, request);
            Assert.Equal(expected, Encoding.UTF8.GetString(response.Body));
            Assert.Equal($"{type}; charset=utf-8", response.ContentType);
            Assert.Equal(response.Body.Length, response.ContentLength);
        }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(500)]
    public void Html_errors_are_the_public_pages(int status)
    {
        using var request = new RailsRequest(Request("GET", "/nope"));
        var response = ReferencePages.For(status, request);
        Assert.Equal(File.ReadAllBytes(Path.Combine(VectorFiles.Root, $"reference/public/{status}.html")), response.Body);
        Assert.Equal("text/html; charset=utf-8", response.ContentType);
    }

    [Fact]
    public void Statuses_without_a_page_are_empty_html_and_head_gets_no_body()
    {
        using var request = new RailsRequest(Request("GET", "/nope"));
        Assert.Equal(new ErrorResponse(400, "text/html; charset=utf-8", 0, []), ReferencePages.For(400, request) with { Body = [] });
        Assert.Empty(ReferencePages.For(501, request).Body);

        using var head = new RailsRequest(Request("HEAD", "/nope.json"));
        var response = ReferencePages.For(404, head);
        Assert.Equal(("application/json; charset=utf-8", 0L), (response.ContentType, response.ContentLength));
    }

    [Fact]
    public async Task An_unknown_path_is_the_404_page()
    {
        var (context, body) = await Serve(Router(), "GET", "/nope");
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(File.ReadAllText(Path.Combine(VectorFiles.Root, "reference/public/404.html")), body);
    }

    [Fact]
    public async Task Routes_without_a_handler_answer_501()
    {
        var (context, body) = await Serve(Router(), "GET", "/rooms/1.json");
        Assert.Equal(501, context.Response.StatusCode);
        Assert.Equal("""{"status":501,"error":"Not Implemented"}""", body);
    }

    [Fact]
    public async Task A_route_to_a_controller_the_reference_lacks_is_a_500()
    {
        var (context, _) = await Serve(new Router(Routes.Table, ReferencePages), "GET", "/rooms/1/settings");
        Assert.Equal(500, context.Response.StatusCode);
    }

    [Fact]
    public async Task Handlers_see_the_route_path_parameters_and_params()
    {
        RailsRequest? seen = null;
        Task Index(HttpContext context)
        {
            seen = context.RailsRequest();
            return Task.CompletedTask;
        }
        var router = Router(definition => definition.Endpoint == "messages#index" ? Index : null);
        var (context, _) = await Serve(router, "GET", "//rooms//1//messages.json?before=5&room_id=x");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("messages#index", seen!.Route!.Endpoint);
        Assert.Equal("1", seen.PathParameters["room_id"]);
        Assert.Equal("/rooms/1/messages.json", seen.Path);
        Assert.Equal(["before=5", "room_id=1", "controller=messages", "action=index", "format=json"], seen.Parameters.Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.Equal(MimeType.Json, seen.Format);
    }

    [Fact]
    public async Task A_posted_method_override_picks_the_route()
    {
        string? endpoint = null;
        var router = Router(definition => context =>
        {
            endpoint = context.RailsRequest().Route!.Endpoint + " " + context.RailsRequest().Method;
            return Task.CompletedTask;
        });
        await Serve(router, "POST", "/session", "_method=delete&x=1", "application/x-www-form-urlencoded");
        Assert.Equal("sessions#destroy DELETE", endpoint);
        await Serve(router, "POST", "/session", "_method=bogus", "application/x-www-form-urlencoded");
        Assert.Equal("sessions#create POST", endpoint);
        await Serve(router, "GET", "/session?_method=delete");
        Assert.Equal("sessions#show GET", endpoint);
    }

    [Fact]
    public async Task Head_matches_get_routes()
    {
        string? method = null;
        var router = Router(definition => context =>
        {
            method = context.RailsRequest().Method;
            return Task.CompletedTask;
        });
        var (context, _) = await Serve(router, "HEAD", "/");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("HEAD", method);
    }

    [Fact]
    public async Task Unknown_verbs_and_bad_parameters_get_their_rails_statuses()
    {
        Assert.Equal(405, (await Serve(Router(), "FOO", "/rooms/1")).Context.Response.StatusCode);
        Assert.Equal(404, (await Serve(Router(), "FOO", "/nope")).Context.Response.StatusCode);
        Assert.Equal(400, (await Serve(Router(), "GET", "/rooms/%FF")).Context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("messages", true)]
    public void Detects_turbo_frame_requests(string? frame, bool expected)
    {
        var context = Request("GET", "/");
        if (frame is not null)
        {
            context.Request.Headers["Turbo-Frame"] = frame;
        }
        using var request = new RailsRequest(context);
        Assert.Equal(expected, request.IsTurboFrameRequest);
        Assert.Equal(frame, request.TurboFrameRequestId);
    }

    static Router Router(Func<RouteDefinition, RequestDelegate?>? handlers = null) =>
        new(new RouteTable(Routes.Definitions, handlers ?? (_ => null)), ReferencePages);

    static async Task<(DefaultHttpContext Context, string Body)> Serve(Router router, string method, string target, string? body = null, string? contentType = null)
    {
        var context = Request(method, target);
        var response = new MemoryStream();
        context.Response.Body = response;
        if (body is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
            context.Request.ContentType = contentType;
        }
        await router.HandleAsync(context);
        return (context, Encoding.UTF8.GetString(response.ToArray()));
    }

    // A request as Kestrel hands it over: RawTarget is the request line's target, undecoded.
    static DefaultHttpContext Request(string method, string target)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        return context;
    }
}
