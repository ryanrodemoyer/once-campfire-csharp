using System.Diagnostics;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Searching;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Tests.State;

public class StateDifferentialTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 3, 2, 16, 0, 0, TimeSpan.Zero);

    public static bool IsReferenceAvailable => CheckReferenceAvailable();

    static bool CheckReferenceAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "image inspect campfire-reference:latest")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task ExportDatabaseForRailsAsync(string targetPath, DateTimeOffset now)
    {
        var oracleFixtures = TestDatabase.Oracle("rails-fixtures.sqlite3");
        File.Copy(oracleFixtures, targetPath, overwrite: true);

        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(targetPath) { Readers = 1 });
        var recordingSeams = new RecordingSeams();
        var seams = recordingSeams.Seams;

        await database.WriteAsync(tx =>
        {
            var designersId = Fixtures.Id("designers");
            var davidId = Fixtures.Id("david");
            var jasonId = Fixtures.Id("jason");

            // 1. Message created by David in Designers
            var message = MessageLifecycle.Create(
                tx,
                seams,
                designersId,
                davidId,
                "csharp-1",
                "Written by <b>C#</b> hovercraft",
                "Written by C# hovercraft",
                now);

            // 2. Boost by Jason with "🦀"
            BoostLifecycle.Create(
                tx,
                message,
                jasonId,
                "🦀",
                "Written by C# hovercraft",
                now);

            // 3. User CSharpUser
            var passwordDigest = SecurePassword.Digest("secret123456", SecurePassword.MinCost)!;
            var user = UserLifecycle.Create(
                tx,
                "CSharpUser",
                "csharp@example.com",
                passwordDigest,
                now);

            // 4. Session
            Sessions.Start(tx.Session, user.Id, "ua", "8.8.8.8", now);

            // 5. Search record
            RecentSearches.Record(tx, user.Id, "hovercraft", now);

            // 6. Closed room with CSharpUser and David
            Rooms.CreateFor(tx.Session, RoomType.Closed, "C# Room", user.Id, [user.Id, davidId], now);

            // 7. Account settings
            var account = Accounts.First(tx.Session)!;
            var settings = account.SettingsData.Assign([new(AccountSettings.RestrictRoomCreationToAdministratorsKey, (System.Text.Json.Nodes.JsonNode)true)]);
            Accounts.Update(tx.Session, account, now, settings: settings);
        }).ConfigureAwait(false);

        // Checkpoint WAL so changes are fully flushed to main database file
        using var conn = new SqliteConnection($"Data Source={targetPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Exported_database_contains_expected_csharp_entities()
    {
        using var testDb = new TestDatabase();
        await ExportDatabaseForRailsAsync(testDb.Path, Now);

        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(testDb.Path) { Readers = 1 });
        await database.ReadAsync(session =>
        {
            // Message check
            var message = session.Query($"SELECT {Message.Columns} FROM messages WHERE client_message_id = @id", Message.Read, ("@id", "csharp-1")).SingleOrDefault();
            Assert.NotNull(message);
            Assert.Equal(Fixtures.Id("designers"), message.RoomId);
            Assert.Equal(Fixtures.Id("david"), message.CreatorId);

            // Rich text check
            var richText = RichTexts.For(session, Message.ModelName, message.Id, "body");
            Assert.NotNull(richText);
            Assert.Equal("Written by <b>C#</b> hovercraft", richText.Body);

            // Search index check
            var searchResults = MessageSearch.Reachable(session, Fixtures.Id("david"), "hovercraft");
            Assert.Contains(message.Id, searchResults.Select(m => m.Id));

            // Boost check
            var boosts = Boosts.ForMessageOrdered(session, message.Id);
            Assert.Single(boosts);
            Assert.Equal("🦀", boosts[0].Content);
            Assert.Equal(Fixtures.Id("jason"), boosts[0].BoosterId);

            // User check
            var user = Users.FindActiveByEmailAddress(session, "csharp@example.com");
            Assert.NotNull(user);
            Assert.Equal("CSharpUser", user.Name);
            Assert.True(SecurePassword.Authenticate(user.PasswordDigest, "secret123456"));

            // Session check
            var sessionList = Sessions.ForUser(session, user.Id);
            Assert.Single(sessionList);
            Assert.Equal("8.8.8.8", sessionList[0].IpAddress);

            // Search record check
            var searches = RecentSearches.Ordered(session, user.Id);
            Assert.Single(searches);
            Assert.Equal("hovercraft", searches[0].Query);

            // Closed room check
            var room = session.Query("SELECT id, name, type FROM rooms WHERE name = 'C# Room'", r => (Id: r.GetInt64(0), Name: r.GetString(1), Type: r.GetString(2))).SingleOrDefault();
            Assert.NotNull(room.Name);
            Assert.Equal("Rooms::Closed", room.Type);

            // Account settings check
            var account = Accounts.First(session);
            Assert.NotNull(account);
            Assert.True(account.SettingsData.RestrictRoomCreationToAdministrators);

            return true;
        }, Ct);
    }

    [Fact]
    public async Task Rails_on_csharp_data_reads_edits_deletes_and_searches()
    {
        Assert.SkipUnless(IsReferenceAvailable, "campfire-reference image is not available");

        var tempDir = Path.Combine(Path.GetTempPath(), "campfire-q03-" + Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "db");
        var storageDir = Path.Combine(tempDir, "storage");
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(storageDir);

        try
        {
            var dbPath = Path.Combine(dbDir, "production.sqlite3");
            await ExportDatabaseForRailsAsync(dbPath, Now);

            var rollbackScript = Path.Combine(TestDatabase.RepositoryRoot, "parity", "state", "rollback.rb");
            Assert.True(File.Exists(rollbackScript), $"rollback.rb missing at {rollbackScript}");

            // Run reference container with the exported database mounted
            var referenceBin = Path.Combine(TestDatabase.RepositoryRoot, "parity", "bin", "reference");
            var psi = new ProcessStartInfo(referenceBin, $"exec --storage \"{tempDir}\" -- bin/rails runner \"/work/parity/state/rollback.rb\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.Environment["PARITY_WORK"] = TestDatabase.RepositoryRoot;

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(Ct);
            var error = await process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);

            Assert.True(process.ExitCode == 0, $"reference runner failed (code {process.ExitCode}):\nOutput: {output}\nError: {error}");
            Assert.Contains("rollback ok", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Schema_identity_matches_rails_prepared_schema()
    {
        using var testDb = new TestDatabase();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(testDb.Path) { Readers = 1, Clock = new FixedClock(Now) });
        var schema = await database.ReadAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
            using var reader = command.ExecuteReader();
            var sb = new System.Text.StringBuilder();
            while (reader.Read())
            {
                sb.Append(reader.GetString(0)).Append(";\n");
            }
            return sb.ToString();
        }, Ct);

        var expectedSchema = await File.ReadAllTextAsync(TestDatabase.Oracle("rails-schema.sql"), Ct);
        Assert.Equal(expectedSchema, schema);
    }
}
