using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Vectors;

namespace Campfire.RichText.Fuzz;

/// <summary>
/// The reference pipeline as a process: <c>Oracle/oracle.rb</c> under <c>bin/rails runner</c> on a
/// fresh schema, in a throwaway <c>campfire-reference</c> instance (<c>parity/bin/reference exec</c>).
/// Bodies go in as JSON lines and the six outputs come back the same way.
/// </summary>
public sealed class ReferenceOracle : IDisposable
{
    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    readonly Process process;
    readonly StringBuilder errors;

    ReferenceOracle(Process process, StringBuilder errors, OracleRecords records)
    {
        this.process = process;
        this.errors = errors;
        Records = records;
    }

    public OracleRecords Records { get; }

    /// <summary>Starts an oracle and waits for it to describe its records.</summary>
    public static ReferenceOracle Start(TimeSpan bootTimeout)
    {
        var root = VectorFiles.Root;
        var script = "/work/tests/Campfire.RichText.Fuzz/Oracle/oracle.rb";
        var start = new ProcessStartInfo(Path.Combine(root, "parity", "bin", "reference"))
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
        };
        foreach (var argument in (string[])["exec", "-e", "RAILS_LOG_LEVEL=fatal", "--", "bash", "-c", $"bin/rails db:schema:load >/dev/null && exec bin/rails runner {script}"])
        {
            start.ArgumentList.Add(argument);
        }
        // One Ruby process uses one CPU; run several oracles for more
        start.Environment["PARITY_CPUS"] = "1";
        start.Environment["PARITY_RUNTIME"] = "docker";

        var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start parity/bin/reference");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            lock (errors)
            {
                errors.AppendLine(e.Data);
            }
        };
        process.BeginErrorReadLine();

        var header = ReadLine(process, bootTimeout)
            ?? throw new InvalidOperationException($"The oracle didn't start:\n{Tail(errors)}");
        return new ReferenceOracle(process, errors, OracleRecords.Parse(header));
    }

    /// <summary>
    /// The reference's outputs for each case, in order. Stops at the first case the oracle doesn't
    /// answer within <paramref name="timeout"/>, after which this oracle is dead (<see cref="IsDead"/>)
    /// and the cases from there on need another.
    /// </summary>
    public List<PipelineOutputs> Ask(IReadOnlyList<FuzzCase> cases, TimeSpan timeout)
    {
        var writer = Task.Run(() =>
        {
            foreach (var c in cases)
            {
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new { id = c.Index, host = c.Host, body = c.Body }));
            }
            process.StandardInput.Flush();
        });

        // Answers come back in order, each after the one before, so each gets the timeout
        var answers = new List<PipelineOutputs>(cases.Count);
        foreach (var c in cases)
        {
            var line = ReadLine(process, timeout);
            if (line is null)
            {
                IsDead = true;
                return answers;
            }
            var answer = JsonNode.Parse(line)!;
            var id = answer["id"]!.GetValue<long>();
            if (id != c.Index)
            {
                throw new InvalidOperationException($"The oracle answered case {id} for case {c.Index}");
            }
            answers.Add(PipelineOutputs.FromReference(answer));
        }
        writer.GetAwaiter().GetResult();
        return answers;
    }

    public bool IsDead { get; private set; }

    static string? ReadLine(Process process, TimeSpan timeout)
    {
        var read = process.StandardOutput.ReadLineAsync();
        return read.Wait(timeout) ? read.Result : null;
    }

    // A hung runner never reads the end of its input, so its container is stopped by hand: the one
    // that mounts this process's throwaway instance (parity/bin/reference names it exec-<pid>-<n>)
    void KillContainer()
    {
        var instance = $"/exec-{process.Id}-";
        foreach (var line in Run("docker", "ps", "-q", "--no-trunc").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Run("docker", "inspect", "-f", "{{range .Mounts}}{{.Source}} {{end}}", line).Contains(instance, StringComparison.Ordinal))
            {
                Run("docker", "kill", line);
            }
        }
    }

    static string Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    static string Tail(StringBuilder errors)
    {
        lock (errors)
        {
            var text = errors.ToString();
            return text.Length > 4000 ? text[^4000..] : text;
        }
    }

    /// <summary>The end of what the oracle wrote to stderr.</summary>
    public string Errors => Tail(errors);

    public void Dispose()
    {
        try
        {
            if (IsDead)
            {
                KillContainer();
            }
            // The runner stops at the end of its input, and the container with it
            process.StandardInput.Close();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        process.Dispose();
    }
}
