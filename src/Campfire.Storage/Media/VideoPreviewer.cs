using System.Diagnostics;

namespace Campfire.Storage.Media;

/// <summary>
/// <c>ActiveStorage::Previewer::VideoPreviewer</c> (activestorage/lib/active_storage/previewer/
/// video_previewer.rb and previewer.rb): ffmpeg draws the relevant frame to stdout as a JPEG.
/// The output is byte-identical to the reference's only with the same ffmpeg build (7.1.5 from
/// Debian trixie, the reference image's), which the image pins (P02).
/// </summary>
public static class VideoPreviewer
{
    /// <summary>
    /// <c>ActiveStorage.video_preview_arguments</c>, Rails' default, which Campfire keeps
    /// (vectors/storage.json records it).
    /// </summary>
    public const string DefaultArguments =
        @"-vf 'select=eq(n\,0)+eq(key\,1)+gt(scene\,0.015),loop=loop=-1:size=2,trim=start_frame=1' -frames:v 1 -f image2";

    /// <summary><c>ActiveStorage.paths[:ffmpeg] || "ffmpeg"</c>.</summary>
    public const string FfmpegPath = "ffmpeg";

    static readonly Lazy<bool> FfmpegExists = new(() => Succeeds(FfmpegPath, "-version"));

    /// <summary><c>ffmpeg_exists?</c>: <c>system(ffmpeg, "-version")</c>, memoized.</summary>
    public static bool IsAvailable => FfmpegExists.Value;

    /// <summary>
    /// <c>accept?(blob)</c>: a video, and ffmpeg runs. S02's <c>Representable.IsPreviewable</c>
    /// assumes ffmpeg, as the reference image has it.
    /// </summary>
    public static bool Accepts(Blobs.Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return blob.IsVideo && IsAvailable;
    }

    /// <summary>
    /// <c>draw_relevant_frame_from(file)</c>: <c>ffmpeg -i &lt;input&gt; &lt;video_preview_arguments&gt; -</c>,
    /// stdout copied to <paramref name="output"/>. A failure raises <c>PreviewError</c> with ffmpeg's
    /// exit status and stderr. Rails waits indefinitely; <paramref name="timeout"/>, when given, kills
    /// ffmpeg once it passes.
    /// </summary>
    public static void DrawRelevantFrame(string input, Stream output, string arguments = DefaultArguments, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        string[] argv = [FfmpegPath, "-i", input, .. Shellwords.Split(arguments), "-"];
        var result = Subprocess.Run(argv, output, timeout);
        if (result.TimedOut)
        {
            throw new PreviewException($"{FfmpegPath} timed out after {timeout}");
        }
        if (result.ExitCode != 0)
        {
            throw new PreviewException($"{FfmpegPath} failed (status {result.ExitCode}): {Chomp(result.Stderr)}");
        }
    }

    /// <summary><c>String#chomp</c>: one trailing <c>\n</c>, <c>\r\n</c> or <c>\r</c>.</summary>
    static string Chomp(string text) =>
        text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') || text.EndsWith('\r') ? text[..^1] : text;

    static bool Succeeds(string program, string argument)
    {
        try
        {
            return Subprocess.Run([program, argument], Stream.Null, null).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>What <see cref="Subprocess.Run"/> saw.</summary>
/// <param name="ExitCode">The exit status, or -1 when killed.</param>
/// <param name="Stderr">Everything the process wrote to stderr.</param>
/// <param name="TimedOut">Whether it was killed for running past the timeout.</param>
public sealed record SubprocessResult(int ExitCode, string Stderr, bool TimedOut);

/// <summary><c>IO.popen(argv, err: tempfile) { |out| IO.copy_stream(out, to) }</c>, with an optional timeout.</summary>
public static class Subprocess
{
    /// <summary>
    /// Runs <paramref name="argv"/> without a shell, stdin closed, stdout copied to
    /// <paramref name="stdout"/> and stderr collected. Throws <see cref="System.ComponentModel.Win32Exception"/>
    /// when the program can't be started (Ruby's <c>Errno::ENOENT</c>).
    /// </summary>
    public static SubprocessResult Run(IReadOnlyList<string> argv, Stream stdout, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(stdout);
        var start = new ProcessStartInfo(argv[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in argv.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? Timeout.InfiniteTimeSpan))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WaitAll(copy, stderr);
            return new SubprocessResult(-1, stderr.Result, TimedOut: true);
        }
        Task.WaitAll(copy, stderr);
        return new SubprocessResult(process.ExitCode, stderr.Result, TimedOut: false);
    }
}
