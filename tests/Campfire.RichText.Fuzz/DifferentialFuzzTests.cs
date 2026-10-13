using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Vectors;

namespace Campfire.RichText.Fuzz;

/// <summary>
/// The differential fuzzer against the reference. The live run needs Docker and the
/// campfire-reference image, so it runs only when asked:
/// <code>CAMPFIRE_FUZZ_ITERATIONS=1000000 dotnet test tests/Campfire.RichText.Fuzz -c Release --filter Live</code>
/// (also <c>CAMPFIRE_FUZZ_SEED</c>, <c>CAMPFIRE_FUZZ_WORKERS</c> and <c>CAMPFIRE_FUZZ_RECORD</c>, the
/// number of cases to record for <see cref="RecordedCorpus"/>). The report goes to <c>tmp/fuzz/</c>.
/// Every <c>bin/check</c> replays the recorded cases instead, and the regression cases: the bodies
/// live runs disagreed on, recorded with the reference's outputs by
/// <c>CAMPFIRE_FUZZ_REGRESSIONS=tmp/fuzz/&lt;run&gt;/findings.jsonl[,...] dotnet test tests/Campfire.RichText.Fuzz --filter Record_regression_cases</c>.
/// </summary>
public partial class DifferentialFuzzTests
{
    [Fact]
    public void Live_differential_fuzzing_finds_no_disagreement()
    {
        var iterations = Setting("CAMPFIRE_FUZZ_ITERATIONS", 0);
        Assert.SkipWhen(iterations == 0, "set CAMPFIRE_FUZZ_ITERATIONS to fuzz against the reference (needs Docker)");

        var seed = Setting("CAMPFIRE_FUZZ_SEED", 1);
        var settings = new FuzzSettings(
            iterations,
            seed,
            (int)Setting("CAMPFIRE_FUZZ_WORKERS", Math.Max(1, Environment.ProcessorCount - 4)),
            Path.Combine(VectorFiles.Root, "tmp", "fuzz", $"seed-{seed}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}"),
            (int)Setting("CAMPFIRE_FUZZ_RECORD", 0));

        var report = new DifferentialFuzzer(settings).Run();

        Assert.Equal(iterations, report.Compared + report.Unanswered);
        Assert.Equal((0L, 0L, 0L), (report.Disagreements, report.SecurityFailures, report.Unanswered));
    }

    [Fact]
    public void Recorded_cases_equal_the_references_outputs()
    {
        var (count, families, failures) = Replay("recorded.jsonl.gz");

        Assert.True(count >= 3_000, $"only {count} recorded cases");
        Assert.Equal(FuzzCorpus.Families.Order(), families.Order());
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} recorded cases fail:\n{string.Join("\n", failures.Take(50))}");
    }

    [Fact]
    public void Regression_cases_equal_the_references_outputs()
    {
        var (count, _, failures) = Replay("regressions.jsonl.gz");

        Assert.True(count > 0);
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} regression cases fail:\n{string.Join("\n", failures.Take(50))}");
    }

    /// <summary>
    /// Asks a fresh reference for its outputs on the bodies of disagreements in the given findings
    /// (a live run's <c>findings.jsonl</c>) and records them as <c>Corpus/regressions.jsonl.gz</c>.
    /// Bodies the report clipped, or too big to commit, are left out, and so are bodies that attach
    /// the fuzzed message itself (from runs before <c>oracle.rb</c> located another one): it renders
    /// itself until Ruby's stack gives out.
    /// </summary>
    [Fact]
    public void Record_regression_cases()
    {
        var findings = Environment.GetEnvironmentVariable("CAMPFIRE_FUZZ_REGRESSIONS");
        Assert.SkipWhen(findings is null, "set CAMPFIRE_FUZZ_REGRESSIONS to findings.jsonl files to record their disagreements (needs Docker)");

        var cases = findings.Split(',')
            .SelectMany(File.ReadLines)
            .Select(line => JsonSerializer.Deserialize<FuzzFinding>(line)!)
            .Where(f => f.Kind.StartsWith("disagreement", StringComparison.Ordinal) && !f.Body.EndsWith(" chars)", StringComparison.Ordinal))
            .Where(f => !AttachesTheFuzzedMessage(f.Body))
            .DistinctBy(f => f.Body)
            .Select(f => new FuzzCase(f.Index, f.Family, f.Host, f.Body))
            .ToList();

        using var oracle = ReferenceOracle.Start(TimeSpan.FromMinutes(5));
        var answers = oracle.Ask(cases, TimeSpan.FromMinutes(10));
        Assert.Equal(cases.Count, answers.Count);
        using var recorder = new Recorder(RecordedCorpus.PathOf("regressions.jsonl.gz"), cases.Count);
        recorder.Start(oracle.Records);
        foreach (var (fuzzCase, answer) in cases.Zip(answers))
        {
            recorder.Record(fuzzCase, answer);
        }
    }

    // The oracle fuzzes Message 1's body
    static bool AttachesTheFuzzedMessage(string body) =>
        SgidAttribute().Matches(body).Any(m => FuzzedMessage().IsMatch(Decoded(m.Groups[1].Value)));

    static string Decoded(string base64)
    {
        var bytes = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, bytes, out var length) ? Encoding.UTF8.GetString(bytes, 0, length) : "";
    }

    [GeneratedRegex("sgid=\"([A-Za-z0-9+/=]+)--")]
    private static partial Regex SgidAttribute();

    [GeneratedRegex("gid://campfire/Message/1(?![0-9])")]
    private static partial Regex FuzzedMessage();

    // Each case's outputs from the port, against the reference's and the security assertions
    static (int Count, List<string> Families, List<string> Failures) Replay(string name)
    {
        var (records, recordedAt, cases) = RecordedCorpus.Load(RecordedCorpus.PathOf(name));
        var path = Path.Combine(Path.GetTempPath(), $"campfire-fuzz-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using var port = records.CreateDatabase(path, () => recordedAt);
            var failures = new List<string>();
            foreach (var (fuzzCase, reference) in cases)
            {
                var outputs = port.Outputs(fuzzCase.Body, fuzzCase.Host);
                if (outputs.Disagreements(reference) is { Count: > 0 } names)
                {
                    failures.Add($"case {fuzzCase.Index} ({fuzzCase.Family}) differs in {string.Join(", ", names)}");
                }
                if (outputs.Presentation.Value is { } html && SecurityAssertions.PresentationViolations(html) is { Count: > 0 } violations)
                {
                    failures.Add($"case {fuzzCase.Index} ({fuzzCase.Family}) presents {string.Join(", ", violations)}");
                }
            }
            return (cases.Count, [.. cases.Select(c => c.Case.Family).Distinct()], failures);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    static long Setting(string name, long fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? long.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
