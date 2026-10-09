using System.Text;
using Campfire.Data.Events;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Csrf;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
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
/// The app as the server builds it for the attachment tests, like <see cref="MessagesApp"/> with
/// the two differences M10 needs: the storage identifies uploaded content with Marcel, as Rails
/// does when it unfurls a blob, and requests can carry multipart bodies as the bytes the vectors
/// build them from. The domain seams are a <see cref="RecordingSeams"/>, or the caller's own (a job
/// runner, for the purge flow).
/// </summary>
sealed class AttachmentsApp : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-web-tests", Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;
    readonly RecordingSeams recording = new();

    public AttachmentsApp(DateTimeOffset now, IEnumerable<string>? fixtures = null, DomainSeams? seams = null)
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
        var (version, revision) = WebApp.VersionFrom(MessagesApp.ParityEnvironment);
        App = new WebApp
        {
            Database = database,
            Keys = Keys,
            Clock = clock,
            AppVersion = version,
            GitRevision = revision,
            Router = new Router(Routes.Table, ErrorPages.FromDirectory(Path.Combine(VectorFiles.Root, "reference/public"))),
            Assets = ReferenceAssets.Bundle,
            Storage = BlobStorage.Local(Path.Combine(directory, "storage"), Keys, Marcel.Identify),
            Seams = seams ?? recording.Seams,
            VapidPublicKey = MessagesApp.ParityEnvironment("VAPID_PUBLIC_KEY"),
        };
    }

    public DateTimeOffset Now { get; }

    public string DatabasePath { get; }

    public string StorageRoot => Path.Combine(directory, "storage");

    public KeyGenerator Keys { get; } = new(MessagesApp.ParityEnvironment("SECRET_KEY_BASE")!);

    public RecordingSeams Recording => recording;

    public WebApp App { get; }

    public SqliteDatabase Database => database;

    /// <summary>
    /// The headers of a browser signed in with <paramref name="sessionToken"/> that sends its CSRF
    /// token as the file uploader's XHR does (<c>X-CSRF-Token</c>, from the page's meta tag; no
    /// token in the multipart body, which carries only the file and the client message id).
    /// </summary>
    public Dictionary<string, string> Uploader(string sessionToken, string accept = "*/*")
    {
        var csrf = AuthenticityToken.GenerateSessionToken();
        var jar = new CookieJar(Keys, () => Now);
        jar.SetAuthenticationCookie(sessionToken);
        jar.Encrypted.Set("_campfire_session", new System.Text.Json.Nodes.JsonObject { ["session_id"] = "0123456789abcdef0123456789abcdef", ["_csrf_token"] = csrf });
        var cookies = string.Join("; ", jar.ToSetCookieHeaders().Select(header => header[..header.IndexOf(';', StringComparison.Ordinal)]));
        return new()
        {
            ["Cookie"] = cookies,
            ["Accept"] = accept,
            ["X-CSRF-Token"] = AuthenticityToken.FormToken(csrf, null, null, "/"),
            ["Origin"] = $"http://{MessagesApp.Host}",
            ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
        };
    }

    /// <summary>A request through the whole app, as Kestrel would hand it over.</summary>
    public async Task<Response> SendAsync(string method, string target, IReadOnlyDictionary<string, string> headers, byte[]? body = null, string? contentType = null)
    {
        var context = new DefaultHttpContext();
        var query = target.IndexOf('?', StringComparison.Ordinal);
        context.Request.Method = method;
        context.Request.Path = PathString.FromUriComponent(query < 0 ? target : target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(target[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.7");
        context.Request.Headers.Host = MessagesApp.Host;
        foreach (var (header, value) in headers)
        {
            context.Request.Headers[header] = value;
        }
        context.Request.ContentType = contentType ?? (body is { Length: > 0 } ? "application/x-www-form-urlencoded" : null);
        body ??= [];
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
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

    public void Dispose()
    {
        database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
