using Campfire.Data.Sqlite;
using Campfire.Vectors;

namespace Campfire.Jobs.Tests.WebPush;

/// <summary>
/// A copy of the database Active Record wrote with reference/test/fixtures loaded
/// (tests/Campfire.Data.Tests/Oracle/rails-fixtures.sqlite3), for one test.
/// </summary>
sealed class FixtureDatabase : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-jobs-tests", Guid.NewGuid().ToString("N"));

    public FixtureDatabase()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "production.sqlite3");
        File.Copy(Path.Combine(VectorFiles.Root, "tests", "Campfire.Data.Tests", "Oracle", "rails-fixtures.sqlite3"), path);
        Db = SqliteDatabase.Open(new SqliteDatabaseOptions(path) { Readers = 1 });
    }

    public SqliteDatabase Db { get; }

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public T Read<T>(Func<SqliteSession, T> work) => Db.ReadAsync(work, Cancellation).GetAwaiter().GetResult();

    public T Write<T>(Func<WriteTransaction, T> work) => Db.WriteAsync(work, Cancellation).GetAwaiter().GetResult();

    public void Execute(string sql, params (string, object?)[] parameters) => Write(tx => tx.Session.Execute(sql, parameters));

    public long Id(string table, string name) =>
        Read(session => session.Query($"SELECT id FROM \"{table}\" WHERE name = @name", reader => reader.GetInt64(0), ("@name", name)).Single());

    public void Dispose()
    {
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
