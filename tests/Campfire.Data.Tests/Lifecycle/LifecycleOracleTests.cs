using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Sqlite;
using SQLitePCL;

namespace Campfire.Data.Tests.Lifecycle;

// Every scenario in Lifecycle/callbacks.json: the writes, transaction boundaries, jobs and
// disconnects the reference's callbacks produced on the parity seed, in order, against what
// Campfire.Data.Lifecycle produces running the same scenarios, in the same order, on the same
// database.
public partial class LifecycleOracleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record Scenario(string Name, JsonObject Input, IReadOnlyList<string> Setup, IReadOnlyList<string> Events, string? Error);

    static readonly Lazy<JsonNode> Oracle = new(() => JsonNode.Parse(File.ReadAllText(Path.Combine(TestDatabase.RepositoryRoot, "tests", "Campfire.Data.Tests", "Lifecycle", "callbacks.json")))!);

    static readonly Lazy<List<Scenario>> Scenarios = new(() =>
        Oracle.Value["scenarios"]!.AsArray().Select(node => new Scenario(
            node!["name"]!.GetValue<string>(),
            node["input"]!.AsObject(),
            [.. node["setup"]!.AsArray().Select(sql => sql!.GetValue<string>())],
            [.. node["events"]!.AsArray().Select(e => e!.GetValue<string>())],
            node["error"]?.GetValue<string>())).ToList());

    static readonly DateTimeOffset Now = DateTimeOffset.Parse(Oracle.Value["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

    // Run once, in the oracle's order, since each scenario starts from the state the last left.
    static readonly Lazy<Task<Dictionary<string, (List<string> Events, string? Error)>>> Results = new(RunAll);

    public static TheoryData<string> ScenarioNames() => [.. Scenarios.Value.Select(s => s.Name)];

    [Fact]
    public void Every_oracle_scenario_has_a_csharp_counterpart()
    {
        var missing = Scenarios.Value.Select(s => s.Name).Except(Runs.Keys).ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Callbacks_run_as_rails_runs_them(string name)
    {
        var expected = Scenarios.Value.Single(s => s.Name == name);
        var (events, error) = (await Results.Value.WaitAsync(Ct))[name];

        Assert.Equal(expected.Error, error);
        Assert.Equal(expected.Events, events);
    }

    [Fact]
    public void Ban_addresses_validate_as_rails_validates_them()
    {
        var mismatches = Oracle.Value["ban_addresses"]!.AsArray()
            .Select(node => (Ip: node!["ip_address"]!.GetValue<string>(), Expected: node["error"]?.GetValue<string>()))
            .Select(c => (c.Ip, c.Expected, Actual: BanAddresses.Error(c.Ip)))
            .Where(c => c.Expected != c.Actual)
            .Select(c => $"{c.Ip}: rails {c.Expected ?? "valid"}, c# {c.Actual ?? "valid"}")
            .ToList();

        Assert.Empty(mismatches);
        Assert.NotEmpty(Oracle.Value["ban_addresses"]!.AsArray());
    }

    static async Task<Dictionary<string, (List<string>, string?)>> RunAll()
    {
        using var file = TestDatabase.FromParitySeed();
        using var database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });
        var recorder = new Recorder();
        await database.WriteAsync(transaction => recorder.Trace(transaction.Session));

        var results = new Dictionary<string, (List<string>, string?)>();
        foreach (var scenario in Scenarios.Value)
        {
            await database.WriteAsync(transaction =>
            {
                foreach (var sql in scenario.Setup)
                {
                    transaction.Session.Execute(sql);
                }
            });
            recorder.Start();
            string? error = null;
            try
            {
                await Runs[scenario.Name](database, recorder.Seams, scenario.Input);
            }
            catch (RecordInvalidException)
            {
                error = "ActiveRecord::RecordInvalid";
            }
            results[scenario.Name] = (recorder.Stop(), error);
        }
        return results;
    }

    delegate Task Run(SqliteDatabase database, DomainSeams seams, JsonObject input);

    static readonly Dictionary<string, Run> Runs = new()
    {
        ["message_create_mentioning_bots"] = CreateMessage,
        ["message_create_in_direct_room_with_bot"] = CreateMessage,
        ["message_create_by_bot_mentioning_itself"] = CreateMessage,
        ["message_create_without_body"] = CreateMessage,
        ["message_update_body"] = async (db, seams, input) =>
        {
            var message = await Read(db, s => Messages.Find(s, Long(input["message_id"]))!);
            await db.WriteAsync(t => MessageLifecycle.UpdateBody(t, message, Text(input["body_html"])!, Text(input["plain_text_body"])!, Now));
        },
        ["boost_create"] = async (db, seams, input) =>
        {
            var message = await Read(db, s => Messages.Find(s, Long(input["message_id"]))!);
            await db.WriteAsync(t => BoostLifecycle.Create(t, message, Long(input["booster_id"]), Text(input["content"])!, "", Now));
        },
        ["boost_destroy"] = async (db, seams, input) =>
        {
            var (boost, message) = await Read(db, s =>
            {
                var boost = Boosts.Find(s, Long(input["boost_id"]))!;
                return (boost, Messages.Find(s, boost.MessageId)!);
            });
            await db.WriteAsync(t => BoostLifecycle.Destroy(t, boost, message, "", Now));
        },
        ["message_destroy"] = async (db, seams, input) =>
        {
            var message = await Read(db, s => Messages.Find(s, Long(input["message_id"]))!);
            await db.WriteAsync(t => MessageLifecycle.Destroy(t, message, Now));
        },
        ["user_create"] = (db, seams, input) =>
            db.WriteAsync(t => UserLifecycle.Create(t, Text(input["name"])!, Text(input["email_address"]), "$2a$12$digest", Now)),
        ["bot_create"] = (db, seams, input) =>
            UserLifecycle.CreateBotAsync(db, Text(input["name"])!, Text(input["webhook_url"]), new FixedClock(Now)),
        ["user_deactivate"] = async (db, seams, input) =>
        {
            var user = await Read(db, s => Users.Find(s, Long(input["user_id"]))!);
            await db.WriteAsync(t => UserLifecycle.Deactivate(t, seams, user, Now));
        },
        ["user_ban"] = Ban,
        ["user_ban_from_private_address"] = Ban,
        ["user_unban"] = async (db, seams, input) =>
        {
            var user = await Read(db, s => Users.Find(s, Long(input["user_id"]))!);
            await db.WriteAsync(t => UserLifecycle.Unban(t, user, Now));
        },
        ["memberships_revise"] = async (db, seams, input) =>
        {
            var room = await Read(db, s => Rooms.Find(s, Long(input["room_id"]))!);
            await db.WriteAsync(t => MembershipLifecycle.Revise(t, seams, room, Longs(input["granted_user_ids"]), Longs(input["revoked_user_ids"])));
        },
        ["memberships_revoke"] = (db, seams, input) =>
            MembershipLifecycle.RevokeFromAsync(db, seams, Long(input["room_id"]), Longs(input["user_ids"])),
    };

    // messages_controller.rb#create: the message, then the bots' webhooks once it's committed.
    static async Task CreateMessage(SqliteDatabase db, DomainSeams seams, JsonObject input)
    {
        var room = await Read(db, s => Rooms.Find(s, Long(input["room_id"]))!);
        await db.WriteAsync(t =>
        {
            var message = MessageLifecycle.Create(t, seams, room.Id, Long(input["creator_id"]), Text(input["client_message_id"]),
                Text(input["body_html"]), Text(input["plain_text_body"])!, Now);
            t.AfterCommit(s => MessageLifecycle.DeliverWebhooksToBots(s, seams.Jobs, room, message, Longs(input["mentioned_user_ids"])));
        });
    }

    static async Task Ban(SqliteDatabase db, DomainSeams seams, JsonObject input)
    {
        var user = await Read(db, s => Users.Find(s, Long(input["user_id"]))!);
        await db.WriteAsync(t => UserLifecycle.Ban(t, seams, user, Now));
    }

    static Task<T> Read<T>(SqliteDatabase db, Func<SqliteSession, T> read) => db.ReadAsync(read, Ct);

    static long Long(JsonNode? node) => node!.GetValue<long>();

    static string? Text(JsonNode? node) => node?.GetValue<string>();

    static List<long> Longs(JsonNode? node) => [.. node!.AsArray().Select(Long)];

    // The seams and the writer connection's statements, in one log, normalized as callbacks.rb
    // normalizes Active Record's.
    sealed class Recorder : IBroadcaster, IJobQueue, IConnectionRevoker
    {
        readonly Lock gate = new();
        List<string>? events;
        sqlite3? connection;

        public Recorder() => Seams = new DomainSeams(this, this, this);

        public DomainSeams Seams { get; }

        // SQLite calls the profile hook as a statement finishes, so `changes()` is its count. Not
        // for an `INSERT ... RETURNING` read through a reader that stops before SQLITE_DONE: its
        // hook runs on reset, before its count is set, so those count the rows the update hook
        // saw them write.
        public void Trace(SqliteSession session)
        {
            connection = session.Connection.Handle!;
            var written = new Dictionary<string, int>(StringComparer.Ordinal);
            raw.sqlite3_update_hook(connection, (_, _, _, table, _) => written[table] = written.GetValueOrDefault(table) + 1, null);
            raw.sqlite3_profile(connection, (_, sql, _) =>
            {
                var rows = raw.sqlite3_changes(connection);
                if (sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase) && InsertPattern().Match(sql) is { Success: true } insert)
                {
                    rows = written.GetValueOrDefault(insert.Groups[1].Value);
                }
                written.Clear();
                Add(Normalize(sql, rows));
            }, null);
        }

        public void Start()
        {
            lock (gate)
            {
                events = [];
            }
        }

        public List<string> Stop()
        {
            lock (gate)
            {
                var recorded = events!;
                events = null;
                return recorded;
            }
        }

        public void Broadcast(string stream, string payload) => Add($"broadcast {stream}");

        public void Enqueue(Job job) => Add($"enqueue {job.ClassName} {string.Join(",", job.ArgumentIds)}");

        public void Disconnect(long userId, bool reconnect) => Add($"disconnect {userId} reconnect={(reconnect ? "true" : "false")}");

        void Add(string? recorded)
        {
            lock (gate)
            {
                if (recorded is not null)
                {
                    events?.Add(recorded);
                }
            }
        }
    }

    // callbacks.rb's normalize_sql.
    static string? Normalize(string sql, int rows)
    {
        if (BeginPattern().IsMatch(sql))
        {
            return "begin";
        }
        if (CommitPattern().IsMatch(sql))
        {
            return "commit";
        }
        if (RollbackPattern().IsMatch(sql))
        {
            return "rollback";
        }
        if (InsertPattern().Match(sql) is { Success: true } insert)
        {
            var columns = insert.Groups[2].Value.Split(',').Select(c => c.Trim().Replace("\"", "", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
            return $"insert {insert.Groups[1].Value} {string.Join(",", columns)} rows={rows}";
        }
        if (UpdatePattern().Match(sql) is { Success: true } update)
        {
            var columns = AssignmentPattern().Matches(update.Groups[2].Value).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal);
            return $"update {update.Groups[1].Value} {string.Join(",", columns)} rows={rows}";
        }
        if (DeletePattern().Match(sql) is { Success: true } delete)
        {
            return $"delete {delete.Groups[1].Value} rows={rows}";
        }
        return null;
    }

    [GeneratedRegex(@"\A\s*BEGIN", RegexOptions.IgnoreCase)]
    private static partial Regex BeginPattern();

    [GeneratedRegex(@"\A\s*COMMIT", RegexOptions.IgnoreCase)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"\A\s*ROLLBACK", RegexOptions.IgnoreCase)]
    private static partial Regex RollbackPattern();

    [GeneratedRegex(@"\A\s*INSERT INTO\s+""?(\w+)""?\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex InsertPattern();

    [GeneratedRegex(@"\A\s*UPDATE\s+""?(\w+)""?\s+SET\s+(.*?)\s+WHERE\s", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UpdatePattern();

    [GeneratedRegex(@"(?:\A|,)\s*""?(\w+)""?\s*=")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"\A\s*DELETE FROM\s+""?(\w+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex DeletePattern();
}
