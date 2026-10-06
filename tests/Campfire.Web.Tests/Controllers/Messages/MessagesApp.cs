using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Csrf;
using Campfire.Storage.Blobs;
using Campfire.Vectors;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;
using Campfire.Web.Tests.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests.Controllers.Messages;

/// <summary>
/// The app as the server builds it, on a copy of the default parity seed
/// (<c>tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3</c>) with the reference's keys, the
/// clock stopped at <see cref="Now"/>, the reference's assets, and domain events recorded in
/// <see cref="Seams"/>.
/// </summary>
sealed class MessagesApp : IDisposable
{
    public const string Host = "campfire.test";

    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-web-tests", Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;

    public MessagesApp(DateTimeOffset now, IEnumerable<string>? fixtures = null)
    {
        Now = now;
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "production.sqlite3");
        File.Copy(Path.Combine(VectorFiles.Root, "tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3"), DatabasePath);
        using (var connection = Open())
        {
            foreach (var fixture in fixtures ?? [])
            {
                using var command = connection.CreateCommand();
                command.CommandText = fixture;
                command.ExecuteNonQuery();
            }
        }
        var clock = new FixedClock(now);
        database = SqliteDatabase.Open(new SqliteDatabaseOptions(DatabasePath) { Clock = clock, Readers = 2 });
        var (version, revision) = WebApp.VersionFrom(ParityEnvironment);
        App = new WebApp
        {
            Database = database,
            Keys = Keys,
            Clock = clock,
            AppVersion = version,
            GitRevision = revision,
            Router = new Router(Routes.Table, ErrorPages.FromDirectory(Path.Combine(VectorFiles.Root, "reference/public"))),
            Assets = ReferenceAssets.Bundle,
            Storage = BlobStorage.Local(Path.Combine(directory, "storage"), Keys),
            Seams = Seams.Seams,
            VapidPublicKey = ParityEnvironment("VAPID_PUBLIC_KEY"),
        };
    }

    public DateTimeOffset Now { get; }

    public string DatabasePath { get; }

    public KeyGenerator Keys { get; } = new(ParityEnvironment("SECRET_KEY_BASE")!);

    public RecordingSeams Seams { get; } = new();

    public WebApp App { get; }

    public SqliteDatabase Database => database;

    /// <summary>
    /// The headers of a browser signed in with <paramref name="sessionToken"/> that sends its CSRF
    /// token as Turbo does (<c>X-CSRF-Token</c>, from the page's meta tag).
    /// </summary>
    public Dictionary<string, string> SignedIn(string sessionToken, string accept = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml")
    {
        var csrf = AuthenticityToken.GenerateSessionToken();
        var jar = new CookieJar(Keys, () => Now);
        jar.SetAuthenticationCookie(sessionToken);
        jar.Encrypted.Set("_campfire_session", new JsonObject { ["session_id"] = "0123456789abcdef0123456789abcdef", ["_csrf_token"] = csrf });
        var cookies = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));
        return new()
        {
            ["Cookie"] = cookies,
            ["Accept"] = accept,
            ["X-CSRF-Token"] = AuthenticityToken.FormToken(csrf, null, null, "/"),
            ["Origin"] = $"http://{Host}",
            ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
        };
    }

    /// <summary>A request through the whole app, as Kestrel would hand it over.</summary>
    public async Task<Response> SendAsync(string method, string target, IReadOnlyDictionary<string, string> headers, string? body = null, string? contentType = null)
    {
        var context = new DefaultHttpContext();
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Method = method;
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.7");
        context.Request.Headers.Host = Host;
        foreach (var (header, value) in headers)
        {
            context.Request.Headers[header] = value;
        }
        context.Request.ContentType = contentType ?? (body is null ? null : "application/x-www-form-urlencoded");
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        await App.HandleAsync(context);
        return new Response(context.Response.StatusCode, context.Response.Headers, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    /// <summary>One value from the database.</summary>
    public object? Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static string? ParityEnvironment(string name)
    {
        foreach (var line in File.ReadLines(Path.Combine(VectorFiles.Root, "parity/.env.reference")))
        {
            if (line.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return line[(name.Length + 1)..];
            }
        }
        return null;
    }

    public void Dispose()
    {
        database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

sealed record Response(int Status, IHeaderDictionary Headers, string Body);
