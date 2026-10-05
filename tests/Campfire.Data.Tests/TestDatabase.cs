namespace Campfire.Data.Tests;

// Paths in the repository, and a scratch directory for databases that is removed afterwards.
sealed class TestDatabase : IDisposable
{
    public static readonly string RepositoryRoot = FindRepositoryRoot();

    public static string Oracle(string name) => System.IO.Path.Combine(RepositoryRoot, "tests", "Campfire.Data.Tests", "Oracle", name);

    public static string Reference(string path) => System.IO.Path.Combine(RepositoryRoot, "reference", path);

    readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "campfire-data-tests", Guid.NewGuid().ToString("N"));

    public TestDatabase()
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "production.sqlite3");
    }

    public string Path { get; }

    // A copy of the database Active Record wrote with reference/test/fixtures loaded.
    public static TestDatabase FromRailsFixtures()
    {
        var database = new TestDatabase();
        File.Copy(Oracle("rails-fixtures.sqlite3"), database.Path);
        return database;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
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

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
