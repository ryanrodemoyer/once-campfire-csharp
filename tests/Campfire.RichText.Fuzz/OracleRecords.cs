using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Attachments;
using Campfire.Vectors;
using Campfire.Web.Helpers;

namespace Campfire.RichText.Fuzz;

/// <summary>A user the oracle created, and the mention markup Rails renders for them.</summary>
public sealed record OracleUser(string Key, long Id, string MentionContent);

/// <summary>An SGID (or GID) the oracle minted, labelled with what it is.</summary>
public sealed record OracleSgid(string Label, string Sgid);

/// <summary>
/// The records the oracle (<c>Oracle/oracle.rb</c>) created, from the first line it prints: raw
/// table rows, its users, and SGIDs minted for every variant.
/// </summary>
public sealed class OracleRecords
{
    // parity/.env.reference: the fixed test key the reference runs with
    static readonly Lazy<string> SecretKeyBase = new(() =>
        File.ReadLines(Path.Combine(VectorFiles.Root, "parity", ".env.reference"))
            .Single(line => line.StartsWith("SECRET_KEY_BASE=", StringComparison.Ordinal))["SECRET_KEY_BASE=".Length..]);

    OracleRecords(JsonObject header)
    {
        Header = header;
        Tables = header["tables"]!.AsObject();
        Users = [.. header["users"]!.AsArray().Select(u => new OracleUser(u!["key"]!.GetValue<string>(), u["id"]!.GetValue<long>(), u["mention_content"]!.GetValue<string>()))];
        Sgids = [.. header["sgids"]!.AsArray().Select(s => new OracleSgid(s!["label"]!.GetValue<string>(), s["sgid"]!.GetValue<string>()))];
    }

    public JsonObject Header { get; }

    public JsonObject Tables { get; }

    public IReadOnlyList<OracleUser> Users { get; }

    public IReadOnlyList<OracleSgid> Sgids { get; }

    public static OracleRecords Parse(string headerLine) => new(JsonNode.Parse(headerLine)!.AsObject());

    /// <summary>
    /// A database holding the same rows, for the port's <see cref="DatabaseAttachables"/>: SGIDs
    /// are verified with the reference's key, and users are read as the app reads them.
    /// </summary>
    /// <param name="path">Where to create the database.</param>
    /// <param name="now">The time SGID expiry is checked against.</param>
    public PortDatabase CreateDatabase(string path, Func<DateTimeOffset> now)
    {
        var database = SqliteDatabase.Open(new SqliteDatabaseOptions(path) { Readers = Environment.ProcessorCount });
        database.WriteAsync(transaction =>
        {
            foreach (var (table, rows) in Tables)
            {
                foreach (var row in rows!.AsArray().Select(r => r!.AsObject()))
                {
                    var columns = row.Select(c => c.Key).ToList();
                    var sql = $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))})";
                    transaction.Session.Execute(sql, [.. columns.Select((c, i) => ($"@p{i}", ColumnValue(row[c])))]);
                }
            }
        }).GetAwaiter().GetResult();
        return new PortDatabase(database, new KeyGenerator(SecretKeyBase.Value), now);
    }

    static object? ColumnValue(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };
}

/// <summary>The port's view of the oracle's records.</summary>
public sealed class PortDatabase(SqliteDatabase database, KeyGenerator keys, Func<DateTimeOffset> now) : IDisposable
{
    /// <summary>The port's six outputs for a body, rendered for a request to <paramref name="host"/>.</summary>
    public PipelineOutputs Outputs(string body, string host) =>
        database.ReadAsync(session =>
        {
            // A verified Message SGID renders messages/_message. The oracle's only message is the
            // one whose body is `body`; its rich text row is not in the exported database.
            var context = new RenderContext(
                new DatabaseAttachables(session, keys, now()),
                host,
                (model, id, ctx) => PipelineOutputs.RenderLocatedMessage(session, keys, model, id, body, ctx));
            return PipelineOutputs.FromPort(body, context);
        }).GetAwaiter().GetResult();

    public void Dispose() => database.Dispose();
}
