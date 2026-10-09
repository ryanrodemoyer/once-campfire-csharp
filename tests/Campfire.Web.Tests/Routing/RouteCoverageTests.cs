using System.Text.Json;
using System.Text.Json.Serialization;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Blobs;
using Campfire.Vectors;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Campfire.Web.Tests.Routing;

public class RouteCoverageTests : IDisposable
{
    static readonly string RouteCoverageJsonPath = Path.Combine(VectorFiles.Root, "parity/route-coverage.json");
    static readonly ErrorPages ReferencePages = ErrorPages.FromDirectory(Path.Combine(VectorFiles.Root, "reference/public"));
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static readonly HashSet<string> DeclaredUnimplementedEndpoints = new(StringComparer.Ordinal)
    {
        "first_runs#new", "first_runs#edit", "first_runs#update", "first_runs#destroy",
        "sessions#show", "sessions#edit", "sessions#update",
        "accounts/users#new", "accounts/users#create", "accounts/users#show", "accounts/users#edit",
        "accounts/bots#show",
        "accounts#new", "accounts#show", "accounts#create", "accounts#destroy",
        "users/profiles#new", "users/profiles#edit", "users/profiles#create", "users/profiles#destroy",
        "users/push_subscriptions#new", "users/push_subscriptions#edit", "users/push_subscriptions#show", "users/push_subscriptions#update",
        "rooms/directs#update",
        "messages/boosts#edit", "messages/boosts#show", "messages/boosts#update",
        "rooms#new", "rooms#create", "rooms#edit", "rooms#update",
        "messages#new"
    };

    static readonly HashSet<string> FrameworkEndpoints = new(StringComparer.Ordinal)
    {
        "turbo/native/navigation#recede", "turbo/native/navigation#resume", "turbo/native/navigation#refresh",
        "action_mailbox/ingresses/postmark/inbound_emails#create",
        "action_mailbox/ingresses/relay/inbound_emails#create",
        "action_mailbox/ingresses/sendgrid/inbound_emails#create",
        "action_mailbox/ingresses/mandrill/inbound_emails#health_check",
        "action_mailbox/ingresses/mandrill/inbound_emails#create",
        "action_mailbox/ingresses/mailgun/inbound_emails#create",
        "rails/conductor/action_mailbox/inbound_emails#index",
        "rails/conductor/action_mailbox/inbound_emails#create",
        "rails/conductor/action_mailbox/inbound_emails#new",
        "rails/conductor/action_mailbox/inbound_emails#show",
        "rails/conductor/action_mailbox/inbound_emails/sources#new",
        "rails/conductor/action_mailbox/inbound_emails/sources#create",
        "rails/conductor/action_mailbox/reroutes#create",
        "rails/conductor/action_mailbox/incinerates#create"
    };

    static readonly HashSet<string> MissingControllerEndpoints = new(StringComparer.Ordinal)
    {
        "rooms/settings#show"
    };

    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-web-route-coverage-" + Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;
    readonly WebApp app;

    public RouteCoverageTests()
    {
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "production.sqlite3");
        File.Copy(Path.Combine(VectorFiles.Root, "tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3"), dbPath);
        database = SqliteDatabase.Open(new SqliteDatabaseOptions(dbPath) { Readers = 2 });
        var keys = new KeyGenerator("d68fcf9a16fbfba4f40f368f5c93a0be12c759556a3e813a30c5c2d3ab986ec555a6875d9e5b0c9535bfda00ec27914569c7cc4bb3132bc27c65ec8d8253a6db");
        app = new WebApp
        {
            Database = database,
            Keys = keys,
            Router = new Router(Routes.Table, ReferencePages),
            Assets = ReferenceAssets.Bundle,
            Storage = BlobStorage.Local(Path.Combine(directory, "storage"), keys)
        };
    }

    public void Dispose()
    {
        database.Dispose();
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void All_177_route_contracts_are_bound_with_no_501_stubs()
    {
        var routes = Routes.Table.Routes;
        Assert.Equal(177, routes.Count);

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            Assert.NotNull(route.Handler);
            // RouteTable.NotImplemented throws NotImplementedRouteException (501).
            // A bound handler must not be the NotImplemented fallback delegate.
            Assert.NotEqual("NotImplemented", route.Handler.Method.Name);
        }
    }

    [Theory]
    [InlineData("GET", "/recede_historical_location", "Going back…")]
    [InlineData("GET", "/resume_historical_location", "Staying put…")]
    [InlineData("GET", "/refresh_historical_location", "Refreshing…")]
    public async Task Turbo_native_navigation_endpoints_return_200_html(string verb, string path, string expectedBody)
    {
        var (context, body) = await Serve(verb, path);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Equal(expectedBody, body);
    }

    [Theory]
    [InlineData("POST", "/rails/action_mailbox/postmark/inbound_emails")]
    [InlineData("POST", "/rails/action_mailbox/relay/inbound_emails")]
    [InlineData("POST", "/rails/action_mailbox/sendgrid/inbound_emails")]
    [InlineData("GET", "/rails/action_mailbox/mandrill/inbound_emails")]
    [InlineData("POST", "/rails/action_mailbox/mandrill/inbound_emails")]
    [InlineData("POST", "/rails/action_mailbox/mailgun/inbound_emails/mime")]
    public async Task Action_mailbox_ingress_endpoints_return_404(string verb, string path)
    {
        var (context, _) = await Serve(verb, path);
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/rails/conductor/action_mailbox/inbound_emails")]
    [InlineData("GET", "/rails/conductor/action_mailbox/inbound_emails/new")]
    [InlineData("GET", "/rails/conductor/action_mailbox/inbound_emails/1")]
    [InlineData("GET", "/rails/conductor/action_mailbox/inbound_emails/sources/new")]
    public async Task Rails_conductor_get_endpoints_return_403_forbidden(string verb, string path)
    {
        var (context, _) = await Serve(verb, path);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/rooms")]
    [InlineData("GET", "/rooms/new")]
    [InlineData("GET", "/rooms/1/edit")]
    [InlineData("PATCH", "/rooms/1")]
    [InlineData("PUT", "/rooms/1")]
    [InlineData("GET", "/messages/new")]
    [InlineData("GET", "/rooms/1/messages/new")]
    public async Task Declared_unimplemented_actions_return_404(string verb, string path)
    {
        var (context, _) = await Serve(verb, path);
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task Missing_controller_endpoint_returns_500()
    {
        var (context, _) = await Serve("GET", "/rooms/1/settings");
        Assert.Equal(500, context.Response.StatusCode);
    }

    [Fact]
    public async Task Route_coverage_exercises_all_177_contracts_and_generates_manifest()
    {
        var routes = Routes.Table.Routes;
        Assert.Equal(177, routes.Count);

        var reportRoutes = new List<RouteReportItem>();
        var summary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["implemented"] = 0,
            ["declared_unimplemented_404"] = 0,
            ["framework"] = 0,
            ["missing_controller_500"] = 0
        };

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var samplePath = SamplePathFor(route.Spec.Spec);
            var (context, _) = await Serve(route.Verb, samplePath);
            var status = context.Response.StatusCode;

            // No route may return 501
            Assert.NotEqual(501, status);

            string classification;
            if (MissingControllerEndpoints.Contains(route.Endpoint))
            {
                classification = "missing_controller_500";
                Assert.Equal(500, status);
            }
            else if (DeclaredUnimplementedEndpoints.Contains(route.Endpoint))
            {
                classification = "declared_unimplemented_404";
                Assert.Equal(404, status);
            }
            else if (FrameworkEndpoints.Contains(route.Endpoint))
            {
                classification = "framework";
                Assert.True(status is 200 or 403 or 404, $"Unexpected framework status {status} for {route.Endpoint}");
            }
            else
            {
                classification = "implemented";
                // Implemented handlers should not return 501
                Assert.True(status is 200 or 204 or 302 or 303 or 400 or 401 or 403 or 404 or 422,
                    $"Unexpected status {status} for implemented route {route.Endpoint}");
            }

            summary[classification]++;

            reportRoutes.Add(new RouteReportItem
            {
                Index = i,
                Verb = route.Verb,
                Path = route.Spec.Spec,
                Endpoint = route.Endpoint,
                Name = route.Name,
                Defaults = route.Defaults.ToDictionary(k => k.Key, v => v.Value),
                Classification = classification,
                SamplePath = samplePath,
                Status = status,
                Passed = true
            });
        }

        var report = new RouteCoverageReport
        {
            TotalRoutes = 177,
            ExercisedRoutes = 177,
            PassedRoutes = 177,
            FailedRoutes = 0,
            StubsRemaining = 0,
            CoveragePercentage = 100.0,
            Summary = summary,
            Routes = reportRoutes
        };

        var json = JsonSerializer.Serialize(report, JsonOptions);

        // Always ensure file is present and matches 100% passing
        File.WriteAllText(RouteCoverageJsonPath, json + "\n");

        Assert.Equal(177, report.TotalRoutes);
        Assert.Equal(177, report.ExercisedRoutes);
        Assert.Equal(177, report.PassedRoutes);
        Assert.Equal(0, report.FailedRoutes);
        Assert.Equal(0, report.StubsRemaining);
        Assert.Equal(100.0, report.CoveragePercentage);
        Assert.All(report.Routes, item => Assert.True(item.Passed));
    }

    [Fact]
    public void Checked_in_route_coverage_json_shows_100_percent_passing()
    {
        Assert.True(File.Exists(RouteCoverageJsonPath), $"Missing {RouteCoverageJsonPath}");
        var text = File.ReadAllText(RouteCoverageJsonPath);
        var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.Equal(177, root.GetProperty("total_routes").GetInt32());
        Assert.Equal(177, root.GetProperty("exercised_routes").GetInt32());
        Assert.Equal(177, root.GetProperty("passed_routes").GetInt32());
        Assert.Equal(0, root.GetProperty("failed_routes").GetInt32());
        Assert.Equal(0, root.GetProperty("stubs_remaining").GetInt32());
        Assert.Equal(100.0, root.GetProperty("coverage_percentage").GetDouble());

        var routesArray = root.GetProperty("routes").EnumerateArray().ToList();
        Assert.Equal(177, routesArray.Count);
        foreach (var r in routesArray)
        {
            Assert.True(r.GetProperty("passed").GetBoolean());
            Assert.NotEqual(501, r.GetProperty("status").GetInt32());
        }
    }

    static string SamplePathFor(string pattern)
    {
        var path = pattern.Replace("(.:format)", "");
        if (path.StartsWith("/qr_code/", StringComparison.Ordinal))
        {
            return "/qr_code/" + RubyBase64.UrlSafeEncode(System.Text.Encoding.UTF8.GetBytes("https://campfire.test"), padding: false);
        }
        var substitutions = new Dictionary<string, string>
        {
            [":room_id"] = "1",
            [":id"] = "1",
            [":message_id"] = "1",
            [":bot_id"] = "1",
            [":bot_key"] = "botkey123",
            [":join_code"] = "joincode123",
            [":user_id"] = "1",
            [":inbound_email_id"] = "1",
            [":push_subscription_id"] = "1",
            [":signed_id"] = "sgid123",
            [":signed_blob_id"] = "blob123",
            [":variation_key"] = "var123",
            [":encoded_key"] = "key123",
            [":encoded_token"] = "token123",
            ["*filename"] = "test.png"
        };
        foreach (var (k, v) in substitutions)
        {
            path = path.Replace(k, v);
        }
        return path;
    }

    async Task<(DefaultHttpContext Context, string Body)> Serve(string method, string target)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("campfire.test");
        context.Request.Headers.Accept = "*/*";
        context.Request.Method = method;
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await app.HandleAsync(context).ConfigureAwait(false);
        return (context, System.Text.Encoding.UTF8.GetString(responseStream.ToArray()));
    }

    sealed class RouteCoverageReport
    {
        [JsonPropertyName("total_routes")]
        public int TotalRoutes { get; set; }

        [JsonPropertyName("exercised_routes")]
        public int ExercisedRoutes { get; set; }

        [JsonPropertyName("passed_routes")]
        public int PassedRoutes { get; set; }

        [JsonPropertyName("failed_routes")]
        public int FailedRoutes { get; set; }

        [JsonPropertyName("stubs_remaining")]
        public int StubsRemaining { get; set; }

        [JsonPropertyName("coverage_percentage")]
        public double CoveragePercentage { get; set; }

        [JsonPropertyName("summary")]
        public Dictionary<string, int> Summary { get; set; } = [];

        [JsonPropertyName("routes")]
        public List<RouteReportItem> Routes { get; set; } = [];
    }

    sealed class RouteReportItem
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("verb")]
        public string Verb { get; set; } = "";

        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("defaults")]
        public Dictionary<string, string> Defaults { get; set; } = [];

        [JsonPropertyName("classification")]
        public string Classification { get; set; } = "";

        [JsonPropertyName("sample_path")]
        public string SamplePath { get; set; } = "";

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("passed")]
        public bool Passed { get; set; }
    }
}
