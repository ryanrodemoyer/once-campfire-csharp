using System.Globalization;
using Campfire.Vectors;

namespace Campfire.RichText.Fuzz;

/// <summary>
/// The differential fuzzer against the reference. The live run needs Docker and the
/// campfire-reference image, so it runs only when asked:
/// <code>CAMPFIRE_FUZZ_ITERATIONS=1000000 dotnet test tests/Campfire.RichText.Fuzz -c Release --filter Live</code>
/// (also <c>CAMPFIRE_FUZZ_SEED</c>, <c>CAMPFIRE_FUZZ_WORKERS</c> and <c>CAMPFIRE_FUZZ_RECORD</c>, the
/// number of cases to record for <see cref="RecordedCorpus"/>). The report goes to <c>tmp/fuzz/</c>.
/// Every <c>bin/check</c> replays the recorded cases instead.
/// </summary>
public class DifferentialFuzzTests
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
        var (records, recordedAt, cases) = RecordedCorpus.Load(RecordedCorpus.PathOf("recorded.jsonl.gz"));
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

            Assert.True(cases.Count >= 3_000, $"only {cases.Count} recorded cases");
            Assert.Equal(FuzzCorpus.Families.Order(), cases.Select(c => c.Case.Family).Distinct().Order());
            Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Count} recorded cases fail:\n{string.Join("\n", failures.Take(50))}");
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
