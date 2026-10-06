using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Media;

/// <summary>
/// Whether this machine has the libvips and ffmpeg that made the vectors (vectors/storage.json
/// <c>versions</c>), which byte-for-byte comparisons need.
/// </summary>
static class Toolchain
{
    const string requireVariable = "CAMPFIRE_REQUIRE_MEDIA_VECTORS";

    static readonly StorageToolVersions Expected = StorageVectors.File.Versions;

    static readonly Lazy<string?> FfmpegVersion = new(() => FirstLine("ffmpeg"));

    static bool Required => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(requireVariable));

    public static void RequireLibVips() => Require(LibVips.IsAvailable, "libvips isn't installed");

    public static void RequireFfmpeg() => Require(VideoPreviewer.IsAvailable && FfmpegVersion.Value is not null, "ffmpeg isn't installed");

    /// <summary>The variant's bytes equal the reference's when libvips is the reference's.</summary>
    public static void CompareImageBytes(string file, StoredBlob expected, string actualPath) =>
        Compare(file, expected, actualPath, LibVips.Version == Expected.Libvips,
            $"the vectors were made with libvips {Expected.Libvips}, this is libvips {LibVips.Version}");

    /// <summary>The preview image's bytes equal the reference's when ffmpeg is the reference's.</summary>
    public static void CompareVideoBytes(string file, StoredBlob expected, string actualPath) =>
        Compare(file, expected, actualPath, FfmpegVersion.Value == Expected.Ffmpeg,
            $"the vectors were made with {Expected.Ffmpeg}, this is {FfmpegVersion.Value}");

    static void Compare(string file, StoredBlob expected, string actualPath, bool sameToolchain, string difference)
    {
        var actual = File.ReadAllBytes(actualPath);
        if (sameToolchain)
        {
            Assert.Equal(expected.Checksum, BlobKey.Checksum(actual));
            Assert.Equal(expected.ByteSize, actual.LongLength);
            Assert.Equal(StorageVectors.ReadFile(file), actual);
            return;
        }
        Require(false, $"bytes not compared: {difference}");
    }

    static void Require(bool condition, string reason)
    {
        if (condition)
        {
            return;
        }
        Assert.False(Required, $"{requireVariable} is set, but {reason}");
        Assert.Skip(reason);
    }

    /// <summary><c>&lt;program&gt; -version</c>'s first line, or null when it doesn't run.</summary>
    static string? FirstLine(string program)
    {
        try
        {
            using var output = new MemoryStream();
            var result = Subprocess.Run([program, "-version"], output, TimeSpan.FromSeconds(30));
            return result.ExitCode == 0 ? System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n')[0].TrimEnd() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
