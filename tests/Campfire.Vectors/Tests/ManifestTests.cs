using System.Diagnostics;
using System.Security.Cryptography;

namespace Campfire.Vectors.Tests;

// The committed vectors must stay byte-identical to the Rust port's at the pinned submodule commit.
public sealed class ManifestTests
{
    static readonly string[] Unlisted = ["MANIFEST", "README.md"];

    [Theory]
    [MemberData(nameof(Manifest.Rows), MemberType = typeof(Manifest))]
    public void VectorMatchesItsRecordedHash(ManifestEntry entry)
    {
        Assert.Equal(entry.Sha256, Sha256(VectorFiles.PathOf(entry.Path)));
    }

    [Theory]
    [MemberData(nameof(Manifest.Rows), MemberType = typeof(Manifest))]
    public void VectorIsByteIdenticalToReferenceRust(ManifestEntry entry)
    {
        var source = Path.Combine(VectorFiles.Root, "reference-rust", entry.Source);
        Assert.SkipUnless(File.Exists(source), "reference-rust is not checked out (git submodule update --init)");

        Assert.Equal(File.ReadAllBytes(source), VectorFiles.ReadBytes(entry.Path));
    }

    [Fact]
    public void ManifestListsEveryVector()
    {
        var files = Directory.GetFiles(VectorFiles.VectorsPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(VectorFiles.VectorsPath, path).Replace('\\', '/'))
            .Except(Unlisted)
            .Order(StringComparer.Ordinal);

        Assert.Equal(files, Manifest.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ManifestRecordsThePinnedReferenceRustCommit()
    {
        var pinned = Git("ls-tree", "HEAD", "reference-rust");
        Assert.SkipWhen(pinned is null, "git metadata is not available");

        // "160000 commit <sha>\treference-rust"
        var sha = pinned.Split([' ', '\t'])[2];
        var header = File.ReadLines(VectorFiles.PathOf("MANIFEST")).First();
        Assert.Contains($"basecamp/once-campfire-rust@{sha}.", header, StringComparison.Ordinal);
    }

    static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static string? Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = VectorFiles.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        try
        {
            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
