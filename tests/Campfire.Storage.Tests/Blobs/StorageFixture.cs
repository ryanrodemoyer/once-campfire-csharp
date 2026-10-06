using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Blobs;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Blobs;

/// <summary>
/// The reference's Active Storage setup for the vectors: the parity <c>secret_key_base</c> (the one
/// vectors/rails_compat.json records, whose prefix vectors/storage.json checks) and
/// <c>ActiveStorage::Current.url_options = { host: "campfire.test", protocol: "http" }</c>.
/// </summary>
static class StorageFixture
{
    public const string BaseUrl = "http://campfire.test";

    public static readonly KeyGenerator Keys = new(SecretKeyBase());

    public static BlobStorage Storage(string root) => BlobStorage.Local(root, Keys);

    public static Blob BlobFrom(StoredBlob row) => new(
        row.Id, row.Key, new Filename(row.Filename), row.ContentType, (JsonObject)JsonNode.Parse(row.Metadata)!,
        row.ServiceName, row.ByteSize, row.Checksum, DateTimeOffset.UnixEpoch);

    public static string Fixture(string name) => Path.Combine(VectorFiles.Root, "reference", "test", "fixtures", "files", name);

    public static DateTimeOffset Time(string iso8601) =>
        DateTimeOffset.Parse(iso8601, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    static string SecretKeyBase()
    {
        var secret = RailsCompatVectors.File.SecretKeyBase;
        Assert.StartsWith(StorageVectors.File.SecretKeyBasePrefix, secret, StringComparison.Ordinal);
        return secret;
    }
}

/// <summary>A scratch storage root and database, removed afterwards.</summary>
sealed class ScratchStorage : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-storage-tests", Guid.NewGuid().ToString("N"));

    public ScratchStorage(string? directory = null)
    {
        if (directory is not null)
        {
            this.directory = directory;
            keep = true;
        }
        Directory.CreateDirectory(this.directory);
        Root = Path.Combine(this.directory, "storage", "files");
        Database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(this.directory, "production.sqlite3")));
        Storage = StorageFixture.Storage(Root);
    }

    readonly bool keep;

    public string Root { get; }

    public SqliteDatabase Database { get; }

    public BlobStorage Storage { get; }

    public void Dispose()
    {
        Database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!keep)
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
