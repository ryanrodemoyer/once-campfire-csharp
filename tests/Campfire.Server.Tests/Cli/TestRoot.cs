using Campfire.Server.Cli;
using Campfire.Web.Assets;

namespace Campfire.Server.Tests.Cli;

// A scratch Rails.root, removed afterwards, with settings pointing into it.
sealed class TestRoot : IDisposable
{
    public static readonly string RepositoryRoot = FindRepositoryRoot();

    static readonly Lazy<AssetBundle> SharedAssets = new(() =>
        AssetBundle.Build(AssetSources.InRepository(RepositoryRoot), new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)));

    public TestRoot()
    {
        Directory.CreateDirectory(Path);
    }

    public static AssetBundle Assets => SharedAssets.Value;

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "campfire-server-tests", Guid.NewGuid().ToString("N"));

    public ServerSettings Settings(params (string Name, string? Value)[] env)
    {
        var variables = env.ToDictionary(variable => variable.Name, variable => variable.Value);
        return ServerSettings.FromEnvironment(name => variables.GetValueOrDefault(name), Path);
    }

    // The database Active Record wrote with reference/test/fixtures loaded (D01's oracle), copied to
    // storage/db/production.sqlite3.
    public string CopySeed()
    {
        var database = System.IO.Path.Combine(Path, "storage", "db", "production.sqlite3");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database)!);
        File.Copy(System.IO.Path.Combine(RepositoryRoot, "tests", "Campfire.Data.Tests", "Oracle", "rails-fixtures.sqlite3"), database);
        return database;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Path, recursive: true);
    }

    static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Campfire.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Campfire.slnx not found above " + AppContext.BaseDirectory);
    }
}
