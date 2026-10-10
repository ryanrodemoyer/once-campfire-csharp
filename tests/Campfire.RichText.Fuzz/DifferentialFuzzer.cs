using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.RichText.Fuzz;

/// <summary>How to run the differential fuzzer.</summary>
/// <param name="Iterations">How many cases to generate and compare.</param>
/// <param name="Seed">The run's seed; case <c>i</c> of a seed is always the same body.</param>
/// <param name="Workers">Reference oracles to run side by side, one CPU each.</param>
/// <param name="ReportDirectory">Where the summary, findings and progress log go.</param>
/// <param name="RecordCases">How many of the first worker's cases to record, with the reference's outputs, for <see cref="RecordedCorpus"/>.</param>
public sealed record FuzzSettings(long Iterations, long Seed, int Workers, string ReportDirectory, int RecordCases = 0)
{
    public int BatchSize { get; init; } = 100;

    /// <summary>How long the reference may take over one case before it's given up on.</summary>
    public TimeSpan CaseTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>A case on which the port and the reference disagree, or whose presentation is unsafe.</summary>
public sealed record FuzzFinding(long Index, string Family, string Host, string Body, string Kind, IReadOnlyList<string> Details);

/// <summary>The outcome of a fuzzing run.</summary>
public sealed class FuzzReport
{
    readonly ConcurrentDictionary<string, long> counters = new();

    public long Compared => Count("compared");

    public long Disagreements => Count("disagreements");

    public long SecurityFailures => Count("security failures");

    public long Unanswered => Count("unanswered");

    public ConcurrentQueue<FuzzFinding> Findings { get; } = new();

    public long Count(string name) => counters.GetValueOrDefault(name);

    public void Add(string name, long by = 1) => counters.AddOrUpdate(name, by, (_, count) => count + by);

    public JsonObject Summary(FuzzSettings settings, TimeSpan elapsed) => new()
    {
        ["seed"] = settings.Seed,
        ["iterations"] = settings.Iterations,
        ["workers"] = settings.Workers,
        ["elapsed_seconds"] = Math.Round(elapsed.TotalSeconds),
        ["counts"] = new JsonObject(counters.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
    };
}

/// <summary>
/// Generates fuzz cases, runs each through the reference (<see cref="ReferenceOracle"/>) and the
/// port, and compares the six outputs. Every presentation, the port's and the reference's, is also
/// checked against <see cref="SecurityAssertions"/>.
/// </summary>
public sealed class DifferentialFuzzer(FuzzSettings settings)
{
    // Findings of one kind beyond this many are counted but not written out
    const int keptPerSignature = 20;

    readonly FuzzReport report = new();
    readonly ConcurrentDictionary<string, int> kept = new();
    long nextBatch;
    StreamWriter? progress;

    public FuzzReport Run()
    {
        Directory.CreateDirectory(settings.ReportDirectory);
        using var log = new StreamWriter(Path.Combine(settings.ReportDirectory, "progress.log")) { AutoFlush = true };
        progress = log;
        var clock = Stopwatch.StartNew();
        Log($"seed {settings.Seed}, {settings.Iterations} iterations, {settings.Workers} workers");

        using var ticker = new Timer(_ => Log($"{report.Compared} compared, {report.Disagreements} disagreements, {report.SecurityFailures} security failures, {report.Unanswered} unanswered, {report.Compared / Math.Max(1, clock.Elapsed.TotalSeconds):F0}/s"), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var workers = Enumerable.Range(0, settings.Workers).Select(n => Task.Factory.StartNew(() => Work(n), TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);

        var summary = report.Summary(settings, clock.Elapsed);
        File.WriteAllText(Path.Combine(settings.ReportDirectory, "summary.json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        using (var findings = new StreamWriter(Path.Combine(settings.ReportDirectory, "findings.jsonl")))
        {
            foreach (var finding in report.Findings.OrderBy(f => f.Index))
            {
                findings.WriteLine(JsonSerializer.Serialize(finding));
            }
        }
        Log($"done: {summary.ToJsonString()}");
        return report;
    }

    void Work(int worker)
    {
        Worker? current = null;
        using var recorder = worker == 0 && settings.RecordCases > 0 ? new Recorder(Path.Combine(settings.ReportDirectory, "recorded.jsonl.gz"), settings.RecordCases) : null;
        try
        {
            while (true)
            {
                var start = Interlocked.Add(ref nextBatch, settings.BatchSize) - settings.BatchSize;
                if (start >= settings.Iterations)
                {
                    break;
                }
                var cases = Enumerable.Range(0, (int)Math.Min(settings.BatchSize, settings.Iterations - start))
                    .Select(i => start + i).ToList();
                while (cases.Count > 0)
                {
                    current ??= Worker.Start(worker, settings, recorder);
                    var batch = cases.Select(current.Corpus.Case).ToList();
                    var answers = current.Oracle.Ask(batch, settings.CaseTimeout);
                    for (var i = 0; i < answers.Count; i++)
                    {
                        Compare(current, batch[i], answers[i]);
                    }
                    cases.RemoveRange(0, answers.Count);
                    if (current.Oracle.IsDead)
                    {
                        // The case it was on is the culprit; the rest go to a fresh oracle
                        Compare(current, batch[answers.Count], null);
                        cases.RemoveAt(0);
                        Log($"worker {worker}: the reference gave up on case {batch[answers.Count].Index} ({batch[answers.Count].Family}, {batch[answers.Count].Body.Length} chars); restarting it\n{current.Oracle.Errors}");
                        current.Dispose();
                        current = null;
                    }
                }
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    void Compare(Worker worker, FuzzCase fuzzCase, PipelineOutputs? reference)
    {
        report.Add($"family {fuzzCase.Family}");
        if (reference is null)
        {
            report.Add("unanswered");
            Keep(new FuzzFinding(fuzzCase.Index, fuzzCase.Family, fuzzCase.Host, fuzzCase.Body, "unanswered", []));
            return;
        }

        var port = worker.Port.Outputs(fuzzCase.Body, fuzzCase.Host);
        report.Add("compared");
        worker.Recorder?.Record(fuzzCase, reference);

        var disagreements = port.Disagreements(reference);
        if (disagreements.Count > 0)
        {
            report.Add("disagreements");
            foreach (var name in disagreements)
            {
                report.Add($"disagreements in {name}");
            }
            var pairs = port.Each().Zip(reference.Each()).Where(p => disagreements.Contains(p.First.Name));
            Keep(new FuzzFinding(fuzzCase.Index, fuzzCase.Family, fuzzCase.Host, fuzzCase.Body, "disagreement " + string.Join(",", disagreements),
                [.. pairs.SelectMany(p => (string[])[$"{p.First.Name} reference: {Clip(p.Second.Output.ToString())}", $"{p.First.Name} port: {Clip(p.First.Output.ToString())}"])]));
        }

        foreach (var (side, presentation) in (ReadOnlySpan<(string, Output)>)[("port", port.Presentation), ("reference", reference.Presentation)])
        {
            if (presentation.Value is { } html && SecurityAssertions.PresentationViolations(html) is { Count: > 0 } violations)
            {
                report.Add($"security failures ({side})");
                if (side == "port")
                {
                    report.Add("security failures");
                }
                Keep(new FuzzFinding(fuzzCase.Index, fuzzCase.Family, fuzzCase.Host, fuzzCase.Body, $"security {side}", [.. violations.Distinct().Take(10)]));
            }
        }
    }

    void Keep(FuzzFinding finding)
    {
        var signature = $"{finding.Family} {finding.Kind}";
        if (kept.AddOrUpdate(signature, 1, (_, n) => n + 1) <= keptPerSignature)
        {
            report.Findings.Enqueue(finding with { Body = Clip(finding.Body, 20_000) });
        }
    }

    static string Clip(string value, int length = 2_000) => value.Length <= length ? value : value[..length] + $"... ({value.Length} chars)";

    void Log(string line)
    {
        lock (report)
        {
            progress?.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
        }
    }

    /// <summary>An oracle, the corpus generated from its records, and the port over the same records.</summary>
    sealed class Worker : IDisposable
    {
        readonly string databasePath;

        Worker(ReferenceOracle oracle, string databasePath, long seed, Recorder? recorder)
        {
            Oracle = oracle;
            this.databasePath = databasePath;
            Corpus = new FuzzCorpus(oracle.Records, seed);
            Port = oracle.Records.CreateDatabase(databasePath, () => DateTimeOffset.UtcNow);
            Recorder = recorder;
            recorder?.Start(oracle.Records);
        }

        public ReferenceOracle Oracle { get; }

        public FuzzCorpus Corpus { get; }

        public PortDatabase Port { get; }

        public Recorder? Recorder { get; }

        public static Worker Start(int number, FuzzSettings settings, Recorder? recorder)
        {
            var path = Path.Combine(settings.ReportDirectory, $"port-{number}-{Guid.NewGuid():N}.sqlite3");
            return new Worker(ReferenceOracle.Start(TimeSpan.FromMinutes(5)), path, settings.Seed, recorder);
        }

        public void Dispose()
        {
            Oracle.Dispose();
            Port.Dispose();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(databasePath)!, Path.GetFileName(databasePath) + "*"))
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>
/// Writes cases with the reference's outputs for <see cref="RecordedCorpus"/>: the oracle's
/// records, then one case per line, gzipped.
/// </summary>
public sealed class Recorder(string path, int limit) : IDisposable
{
    StreamWriter? writer;
    int recorded;

    public void Start(OracleRecords records)
    {
        if (writer is not null)
        {
            return;
        }
        writer = new StreamWriter(new GZipStream(File.Create(path), CompressionLevel.SmallestSize));
        var header = records.Header.DeepClone().AsObject();
        header["recorded_at"] = DateTimeOffset.UtcNow.ToString("O");
        writer.WriteLine(header.ToJsonString());
    }

    public void Record(FuzzCase fuzzCase, PipelineOutputs reference)
    {
        // Huge bodies would bloat the committed file; the live runs cover them
        if (writer is null || recorded >= limit || fuzzCase.Body.Length > 64 * 1024)
        {
            return;
        }
        recorded++;
        writer.WriteLine(new JsonObject
        {
            ["index"] = fuzzCase.Index,
            ["family"] = fuzzCase.Family,
            ["host"] = fuzzCase.Host,
            ["body"] = fuzzCase.Body,
            ["reference"] = RecordedCorpus.ToJson(reference),
        }.ToJsonString());
    }

    public void Dispose() => writer?.Dispose();
}
